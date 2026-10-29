using Microsoft.EntityFrameworkCore;
using StokvelSync.Api.Data;
using StokvelSync.Api.Domain;
namespace StokvelSync.Api.Services;

public class RotationService(AppDbContext db)
{
    /// Builds one slot per member. Optional `order` (user ids) sets the payout sequence; otherwise join order.
    public async Task<(bool ok, string error)> GenerateAsync(Guid stokvelId, List<Guid>? order, CancellationToken ct)
    {
        var s = await db.Stokvels.FindAsync([stokvelId], ct);
        if (s is null) return (false, "not_found");
        var existing = await db.RotationSlots.Where(r => r.StokvelId == stokvelId).ToListAsync(ct);
        if (existing.Any(r => r.PaidOut)) return (false, "rotation_in_progress");
        db.RotationSlots.RemoveRange(existing);

        var members = await db.Memberships.Where(m => m.StokvelId == stokvelId && m.Active).OrderBy(m => m.JoinedAt).ToListAsync(ct);
        if (order is not null)
        {
            if (order.Count != members.Count || !members.All(m => order.Contains(m.UserId))) return (false, "order_must_list_every_member");
            members = members.OrderBy(m => order.IndexOf(m.UserId)).ToList();
        }
        for (var i = 0; i < members.Count; i++)
        {
            members[i].RotationPosition = i + 1;
            db.RotationSlots.Add(new RotationSlot
            {
                StokvelId = stokvelId, CycleNumber = i + 1, RecipientUserId = members[i].UserId,
                DueDate = s.Frequency switch
                {
                    Frequency.Weekly => s.StartDate.AddDays(7 * i),
                    Frequency.Fortnightly => s.StartDate.AddDays(14 * i),
                    _ => s.StartDate.AddMonths(i)
                }
            });
        }
        await db.SaveChangesAsync(ct);
        return (true, "");
    }
}
