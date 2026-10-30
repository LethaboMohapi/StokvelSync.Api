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
        if (existing is not null)
            return existing.UserId == userId && existing.StokvelId == stokvelId
                ? (true, "duplicate", existing)      // safe retry from offline queue
                : (false, "invalid_op", null);       // key belongs to someone else: reveal nothing

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
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)   // two retries of the same OpId raced; the unique index let one win
        {
            db.Entry(c).State = EntityState.Detached;
            var dup = await db.Contributions.FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey && x.UserId == userId && x.StokvelId == stokvelId, ct);
            if (dup is not null) return (true, "duplicate", dup);
            throw;
        }

        var r = await pay.InitiateAsync(new PaymentRequest(user.Phone, c.Amount, c.Id.ToString()), ct);
        c.Status = r.Accepted ? PaymentStatus.Pending : PaymentStatus.Failed;
        c.ProviderRef = r.Accepted ? r.ProviderRef : null;
        await db.SaveChangesAsync(ct);
        return (r.Accepted, r.Accepted ? "pending" : "payment_failed", c);
    }

    public async Task<bool> ConfirmAsync(string providerRef, bool success, CancellationToken ct)
    {
        var c = await db.Contributions.AsNoTracking().FirstOrDefaultAsync(x => x.ProviderRef == providerRef, ct);
        if (c is null) return false;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Atomic claim: only one caller can flip Pending -> settled, so repeated or concurrent webhooks can't double-credit.
        var newStatus = success ? PaymentStatus.Confirmed : PaymentStatus.Failed;
        var claimed = await db.Contributions.Where(x => x.Id == c.Id && x.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, newStatus), ct);
        if (claimed == 0) return false;   // already settled (webhooks repeat)

        // Status change and ledger entry commit together or not at all.
        if (success) await ledger.AppendAsync(c.StokvelId, c.UserId, LedgerType.Contribution, c.Amount, c.CycleNumber, ct);
        await tx.CommitAsync(ct);
        if (!success) return true;

        var user = (await db.Users.FindAsync([c.UserId], ct))!;
        var s = (await db.Stokvels.FindAsync([c.StokvelId], ct))!;
        await sms.SendAsync(user.Phone, t.T("contribution_received", user.Language, c.Amount.ToString("F2"), s.Name), ct);
        return true;
    }
}
