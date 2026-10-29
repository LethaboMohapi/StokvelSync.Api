using Microsoft.EntityFrameworkCore;
using StokvelSync.Api.Data;
using StokvelSync.Api.Domain;
namespace StokvelSync.Api.Services;

/// Implement for BulkSMS, Clickatell, Africa's Talking, etc.
public interface ISmsGateway { Task SendAsync(string phone, string text, CancellationToken ct); }

public class LogSmsGateway(ILogger<LogSmsGateway> log) : ISmsGateway
{
    public Task SendAsync(string phone, string text, CancellationToken ct)
    { log.LogInformation("SMS -> {Phone}: {Text}", phone, text); return Task.CompletedTask; }
}

/// Feature-phone friendly: BAL <code> | PAY <code> | NEXT <code> | LANG EN|ZU|XH|AF
public class SmsCommandProcessor(AppDbContext db, ContributionService contributions, ISmsGateway sms, Localizer t)
{
    public async Task HandleAsync(string from, string body, CancellationToken ct)
    {
        var phone = Phone.Normalize(from);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == phone, ct);
        if (user is null) { await sms.SendAsync(phone, t.T("unregistered", Language.En), ct); return; }

        var parts = body.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts.FirstOrDefault()?.ToUpperInvariant();
        var arg = parts.Skip(1).FirstOrDefault()?.ToUpperInvariant();
        string reply;

        if (cmd == "LANG" && arg is not null)
        {
            user.Language = Localizer.Parse(arg, user.Language);
            await db.SaveChangesAsync(ct);
            reply = t.T("lang_set", user.Language);
        }
        else if (cmd is "BAL" or "PAY" or "NEXT" && arg is not null)
        {
            var s = await db.Stokvels.FirstOrDefaultAsync(x => x.JoinCode == arg, ct);
            var isMember = s is not null && await db.Memberships.AnyAsync(m => m.StokvelId == s.Id && m.UserId == user.Id && m.Active, ct);
            if (!isMember) reply = t.T("not_member", user.Language, arg);
            else if (cmd == "BAL")
            {
                var bal = await db.Ledger.Where(l => l.StokvelId == s!.Id).SumAsync(l => l.Amount, ct);
                reply = t.T("balance", user.Language, s!.Name, bal.ToString("F2"));
            }
            else if (cmd == "NEXT")
            {
                var slot = await db.RotationSlots.Where(r => r.StokvelId == s!.Id && !r.PaidOut).OrderBy(r => r.CycleNumber).FirstOrDefaultAsync(ct);
                if (slot is null) reply = t.T("no_payouts", user.Language);
                else
                {
                    var who = await db.Users.Where(u => u.Id == slot.RecipientUserId).Select(u => u.FullName).FirstAsync(ct);
                    reply = t.T("next_payout", user.Language, slot.CycleNumber, who, slot.DueDate.ToString("dd MMM yyyy"));
                }
            }
            else // PAY: one SMS contribution per member per cycle (deterministic idempotency key)
            {
                var cycle = await contributions.CurrentCycleAsync(s!.Id, ct);
                var key = ContributionService.DeterministicKey($"{user.Id}:{s.Id}:{cycle}:sms");
                var r = await contributions.InitiateAsync(user.Id, s.Id, key, Channel.Sms, ct);
                reply = r.ok ? t.T("payment_prompt", user.Language, s.ContributionAmount.ToString("F2")) : t.T("payment_failed", user.Language);
            }
        }
        else reply = t.T("unknown_command", user.Language);

        await sms.SendAsync(phone, reply, ct);
    }
}

public static class Phone
{
    /// 0821234567 / 27821234567 / +27 82 123 4567 -> +27821234567
    public static string Normalize(string raw)
    {
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (d.StartsWith("0")) d = "27" + d[1..];
        return "+" + d;
    }
}
