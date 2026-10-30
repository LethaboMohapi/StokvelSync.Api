using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StokvelSync.Api.Data;
using StokvelSync.Api.Domain;
namespace StokvelSync.Api.Services;

public class LedgerService(AppDbContext db)
{
    /// Row-lock the stokvel so only one writer at a time can extend its ledger chain. Call inside a transaction.
    public async Task LockAsync(Guid stokvelId, CancellationToken ct = default) =>
        await db.Stokvels.FromSqlInterpolated($"SELECT * FROM `Stokvels` WHERE `Id` = {stokvelId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);

    public async Task<LedgerEntry> AppendAsync(Guid stokvelId, Guid userId, LedgerType type, decimal amount, int cycle, CancellationToken ct = default)
    {
        // Join the caller's transaction if there is one, otherwise own a short one.
        await using var ownTx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockAsync(stokvelId, ct);

        var prev = await db.Ledger.Where(l => l.StokvelId == stokvelId).OrderByDescending(l => l.Id)
            .Select(l => l.Hash).FirstOrDefaultAsync(ct) ?? "GENESIS";
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var e = new LedgerEntry { StokvelId = stokvelId, UserId = userId, Type = type, Amount = amount, CycleNumber = cycle, CreatedAt = now, PrevHash = prev };
        e.Hash = Compute(e);
        db.Ledger.Add(e);
        await db.SaveChangesAsync(ct);
        if (ownTx is not null) await ownTx.CommitAsync(ct);
        return e;
    }

    public async Task<bool> VerifyAsync(Guid stokvelId, CancellationToken ct = default)
    {
        var prev = "GENESIS";
        await foreach (var e in db.Ledger.Where(l => l.StokvelId == stokvelId).OrderBy(l => l.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (e.PrevHash != prev || e.Hash != Compute(e)) return false;
            prev = e.Hash;
        }
        return true;
    }

    static string Compute(LedgerEntry e)
    {
        var s = string.Join("|", e.PrevHash, e.StokvelId, e.UserId, (int)e.Type,
            e.Amount.ToString("F2", CultureInfo.InvariantCulture), e.CycleNumber,
            e.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }
}
