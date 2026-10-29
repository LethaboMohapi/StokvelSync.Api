using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StokvelSync.Api.Data;
using StokvelSync.Api.Domain;
namespace StokvelSync.Api.Services;

/// Single entry point for contributions from the app, offline sync and SMS.
public class ContributionService(AppDbContext db, IPaymentProvider pay, LedgerService ledger, ISmsGateway sms, Localizer t)
{
    public static Guid DeterministicKey(string s) => new(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    public async Task<int> CurrentCycleAsync(Guid stokvelId, CancellationToken ct) =>
        await db.RotationSlots.CountAsync(r => r.StokvelId == stokvelId && r.PaidOut, ct) + 1;

    public async Task<(bool ok, string code, Contribution? contribution)> InitiateAsync(Guid userId, Guid stokvelId, Guid idempotencyKey, Channel channel, CancellationToken ct)
    {
        var existing = await db.Contributions.FirstOrDefaultAsync(c => c.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return (true, "duplicate", existing);   // safe retry from offline queue

        var isMember = await db.Memberships.AnyAsync(m => m.StokvelId == stokvelId && m.UserId == userId && m.Active, ct);
        if (!isMember) return (false, "not_member", null);

        var s = (await db.Stokvels.FindAsync([stokvelId], ct))!;
        var user = (await db.Users.FindAsync([userId], ct))!;
        var c = new Contribution
        {
            StokvelId = stokvelId, UserId = userId, Amount = s.ContributionAmount, Channel = channel,
            IdempotencyKey = idempotencyKey, CycleNumber = await CurrentCycleAsync(stokvelId, ct), Status = PaymentStatus.Pending
        };
        db.Contributions.Add(c);
        await db.SaveChangesAsync(ct);

        var r = await pay.InitiateAsync(new PaymentRequest(user.Phone, c.Amount, c.Id.ToString()), ct);
        c.Status = r.Accepted ? PaymentStatus.Pending : PaymentStatus.Failed;
        c.ProviderRef = r.Accepted ? r.ProviderRef : null;
        await db.SaveChangesAsync(ct);
        return (r.Accepted, r.Accepted ? "pending" : "payment_failed", c);
    }

    public async Task<bool> ConfirmAsync(string providerRef, bool success, CancellationToken ct)
    {
        var c = await db.Contributions.FirstOrDefaultAsync(x => x.ProviderRef == providerRef, ct);
        if (c is null || c.Status != PaymentStatus.Pending) return false;   // unknown or already settled (webhooks repeat)
        c.Status = success ? PaymentStatus.Confirmed : PaymentStatus.Failed;
        await db.SaveChangesAsync(ct);
        if (!success) return true;

        await ledger.AppendAsync(c.StokvelId, c.UserId, LedgerType.Contribution, c.Amount, c.CycleNumber, ct);
        var user = (await db.Users.FindAsync([c.UserId], ct))!;
        var s = (await db.Stokvels.FindAsync([c.StokvelId], ct))!;
        await sms.SendAsync(user.Phone, t.T("contribution_received", user.Language, c.Amount.ToString("F2"), s.Name), ct);
        return true;
    }
}
