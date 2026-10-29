using System.ComponentModel.DataAnnotations;
namespace StokvelSync.Api.Domain;

public enum Language { En, Zu, Xh, Af }
public enum MemberRole { Member, Treasurer, Chair }
public enum Frequency { Weekly, Fortnightly, Monthly }
public enum PaymentStatus { Pending, Confirmed, Failed }
public enum LedgerType { Contribution, Payout }
public enum Channel { App, Sms, Offline }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(16)] public string Phone { get; set; } = "";      // E.164, e.g. +27821234567
    [MaxLength(120)] public string FullName { get; set; } = "";
    public string PinHash { get; set; } = "";
    public Language Language { get; set; } = Language.En;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Stokvel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(12)] public string JoinCode { get; set; } = "";   // also used as the SMS group code
    public decimal ContributionAmount { get; set; }               // ZAR
    public Frequency Frequency { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Membership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StokvelId { get; set; }
    public Guid UserId { get; set; }
    public MemberRole Role { get; set; }
    public int? RotationPosition { get; set; }
    public bool Active { get; set; } = true;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public class Contribution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StokvelId { get; set; }
    public Guid UserId { get; set; }
    public decimal Amount { get; set; }
    public int CycleNumber { get; set; }
    public PaymentStatus Status { get; set; }
    public Channel Channel { get; set; }
    public Guid IdempotencyKey { get; set; }                      // client opId makes offline sync safe to retry
    [MaxLength(64)] public string? ProviderRef { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RotationSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StokvelId { get; set; }
    public int CycleNumber { get; set; }
    public Guid RecipientUserId { get; set; }
    public DateTime DueDate { get; set; }
    public bool PaidOut { get; set; }
}

/// Append-only, hash-chained ledger: each row commits to the previous one, so tampering is detectable.
public class LedgerEntry
{
    public long Id { get; set; }
    public Guid StokvelId { get; set; }
    public Guid UserId { get; set; }
    public LedgerType Type { get; set; }
    public decimal Amount { get; set; }                           // + money in, - money out
    public int CycleNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    [MaxLength(64)] public string PrevHash { get; set; } = "";
    [MaxLength(64)] public string Hash { get; set; } = "";
}
