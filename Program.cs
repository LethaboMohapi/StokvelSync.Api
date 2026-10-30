using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StokvelSync.Api.Data;
using StokvelSync.Api.Domain;
using StokvelSync.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------- Fail fast on missing / placeholder secrets ----------
static bool Weak(string? v, int min) => string.IsNullOrWhiteSpace(v) || v.Length < min || v.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
var weak = new List<string>();
if (Weak(cfg["Jwt:Key"], 32)) weak.Add("Jwt:Key");
if (Weak(cfg["Payments:WebhookSecret"], 16)) weak.Add("Payments:WebhookSecret");
if (Weak(cfg["Sms:InboundKey"], 16)) weak.Add("Sms:InboundKey");
if (weak.Count > 0)
{
    var msg = $"Missing or placeholder secrets: {string.Join(", ", weak)}. Set them via dotnet user-secrets or environment variables.";
    if (builder.Environment.IsDevelopment()) Console.WriteLine("WARNING: " + msg);
    else throw new InvalidOperationException(msg);
}

// Constant-time comparison for shared secrets (hash first so lengths match).
static bool SecretEquals(string? supplied, string? expected) =>
    !string.IsNullOrEmpty(supplied) && !string.IsNullOrEmpty(expected) &&
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseMySql(cfg.GetConnectionString("Default"), new MySqlServerVersion(new Version(8, 0, 36))));
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<RotationService>();
builder.Services.AddScoped<ContributionService>();
builder.Services.AddScoped<SmsCommandProcessor>();
builder.Services.AddSingleton<Localizer>();
builder.Services.AddSingleton<IPaymentProvider, SandboxPaymentProvider>();
builder.Services.AddSingleton<ISmsGateway, LogSmsGateway>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Slows PIN guessing. Behind a reverse proxy, enable ForwardedHeaders so this sees the real client IP.
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = cfg["Jwt:Issuer"], ValidAudience = cfg["Jwt:Issuer"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!))
    });

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger(); app.UseSwaggerUI();
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated(); // swap for migrations in prod
}
app.UseDefaultFiles();
app.UseStaticFiles();   // serves wwwroot/index.html at /
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

Guid Uid(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
string Token(User u)
{
    var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!)), SecurityAlgorithms.HmacSha256);
    var jwt = new JwtSecurityToken(cfg["Jwt:Issuer"], cfg["Jwt:Issuer"], [new Claim(ClaimTypes.NameIdentifier, u.Id.ToString())],
        expires: DateTime.UtcNow.AddHours(12), signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(jwt);
}
async Task<Membership?> Mem(AppDbContext db, Guid stokvelId, Guid uid) =>
    await db.Memberships.FirstOrDefaultAsync(m => m.StokvelId == stokvelId && m.UserId == uid && m.Active);
bool IsAdmin(Membership? m) => m is { Role: MemberRole.Chair or MemberRole.Treasurer };

// ---------- Auth ----------
app.MapPost("/api/auth/register", async (RegisterDto d, AppDbContext db) =>
{
    if (d.Pin.Length is < 4 or > 6 || !d.Pin.All(char.IsDigit)) return Results.BadRequest("pin_must_be_4_to_6_digits");
    var phone = Phone.Normalize(d.Phone);
    if (await db.Users.AnyAsync(u => u.Phone == phone)) return Results.Conflict("phone_taken");
    var u = new User { Phone = phone, FullName = d.FullName, PinHash = BCrypt.Net.BCrypt.HashPassword(d.Pin), Language = Localizer.Parse(d.Language, Language.En) };
    db.Users.Add(u); await db.SaveChangesAsync();
    return Results.Ok(new { token = Token(u) });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (LoginDto d, AppDbContext db) =>
{
    var phone = Phone.Normalize(d.Phone);
    var u = await db.Users.FirstOrDefaultAsync(x => x.Phone == phone);
    return u is null || !BCrypt.Net.BCrypt.Verify(d.Pin, u.PinHash) ? Results.Unauthorized() : Results.Ok(new { token = Token(u) });
}).RequireRateLimiting("auth");

// ---------- Stokvels ----------
app.MapPost("/api/stokvels", async (CreateStokvelDto d, ClaimsPrincipal p, AppDbContext db) =>
{
    var s = new Stokvel
    {
        Name = d.Name, ContributionAmount = d.ContributionAmount, Frequency = d.Frequency, StartDate = d.StartDate.Date,
        JoinCode = new string(RandomNumberGenerator.GetItems<char>("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6))
    };
    db.Stokvels.Add(s);
    db.Memberships.Add(new Membership { StokvelId = s.Id, UserId = Uid(p), Role = MemberRole.Chair });
    await db.SaveChangesAsync();
    return Results.Ok(new { s.Id, s.Name, s.JoinCode });
}).RequireAuthorization();

app.MapPost("/api/stokvels/join", async (JoinDto d, ClaimsPrincipal p, AppDbContext db) =>
{
    var s = await db.Stokvels.FirstOrDefaultAsync(x => x.JoinCode == d.JoinCode.ToUpperInvariant());
    if (s is null) return Results.NotFound();
    if (await Mem(db, s.Id, Uid(p)) is not null) return Results.Conflict("already_member");
    db.Memberships.Add(new Membership { StokvelId = s.Id, UserId = Uid(p), Role = MemberRole.Member });
    await db.SaveChangesAsync();
    return Results.Ok(new { s.Id, s.Name });
}).RequireAuthorization();

// ---------- Rotation ----------
app.MapPost("/api/stokvels/{id:guid}/rotation/generate", async (Guid id, GenerateRotationDto? d, ClaimsPrincipal p, AppDbContext db, RotationService rot, CancellationToken ct) =>
{
    if (!IsAdmin(await Mem(db, id, Uid(p)))) return Results.Forbid();
    var r = await rot.GenerateAsync(id, d?.Order, ct);
    return r.ok ? Results.Ok() : Results.BadRequest(r.error);
}).RequireAuthorization();

app.MapGet("/api/stokvels/{id:guid}/rotation", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (await Mem(db, id, Uid(p)) is null) return Results.Forbid();
    var slots = await (from r in db.RotationSlots.Where(r => r.StokvelId == id).OrderBy(r => r.CycleNumber)
                       join u in db.Users on r.RecipientUserId equals u.Id
                       select new { r.CycleNumber, Recipient = u.FullName, r.DueDate, r.PaidOut }).ToListAsync();
    return Results.Ok(slots);
}).RequireAuthorization();

app.MapPost("/api/stokvels/{id:guid}/rotation/{cycle:int}/payout", async (Guid id, int cycle, ClaimsPrincipal p, AppDbContext db, LedgerService ledger, CancellationToken ct) =>
{
    if (!IsAdmin(await Mem(db, id, Uid(p)))) return Results.Forbid();

    await using var tx = await db.Database.BeginTransactionAsync(ct);
    await ledger.LockAsync(id, ct);   // serialises payouts and ledger writes for this stokvel; a double-click waits, then sees PaidOut

    var slot = await db.RotationSlots.FirstOrDefaultAsync(r => r.StokvelId == id && r.CycleNumber == cycle, ct);
    if (slot is null || slot.PaidOut) return Results.BadRequest("invalid_slot");

    var current = await db.RotationSlots.CountAsync(r => r.StokvelId == id && r.PaidOut, ct) + 1;
    if (cycle != current) return Results.BadRequest("not_current_cycle");

    var members = await db.Memberships.CountAsync(m => m.StokvelId == id && m.Active, ct);
    var paidMembers = await db.Contributions
        .Where(c => c.StokvelId == id && c.CycleNumber == cycle && c.Status == PaymentStatus.Confirmed)
        .Select(c => c.UserId).Distinct().CountAsync(ct);
    if (paidMembers < members) return Results.BadRequest("cycle_incomplete");

    var pot = await db.Contributions.Where(c => c.StokvelId == id && c.CycleNumber == cycle && c.Status == PaymentStatus.Confirmed).SumAsync(c => c.Amount, ct);
    slot.PaidOut = true;
    await ledger.AppendAsync(id, slot.RecipientUserId, LedgerType.Payout, -pot, cycle, ct); // saves slot change too, inside this transaction
    await tx.CommitAsync(ct);
    return Results.Ok(new { cycle, paidOut = pot });
}).RequireAuthorization();

// ---------- Contributions & ledger ----------
app.MapPost("/api/stokvels/{id:guid}/contributions", async (Guid id, ContributeDto d, ClaimsPrincipal p, ContributionService cs, CancellationToken ct) =>
{
    var r = await cs.InitiateAsync(Uid(p), id, d.OpId ?? Guid.NewGuid(), Channel.App, ct);
    return r.ok ? Results.Accepted(value: new { r.code, contributionId = r.contribution!.Id }) : Results.BadRequest(r.code);
}).RequireAuthorization();

app.MapGet("/api/stokvels/{id:guid}/ledger", async (Guid id, string? lang, ClaimsPrincipal p, AppDbContext db, LedgerService ledger, Localizer t, CancellationToken ct) =>
{
    var uid = Uid(p);
    if (await Mem(db, id, uid) is null) return Results.Forbid();
    var me = await db.Users.FindAsync([uid], ct);
    var l = Localizer.Parse(lang, me!.Language);
    var rows = await (from e in db.Ledger.Where(e => e.StokvelId == id).OrderByDescending(e => e.Id)
                      join u in db.Users on e.UserId equals u.Id
                      select new { e.Id, Member = u.FullName, e.Type, e.Amount, e.CycleNumber, e.CreatedAt, e.Hash }).Take(200).ToListAsync(ct);
    return Results.Ok(new
    {
        chainValid = await ledger.VerifyAsync(id, ct),
        balance = await db.Ledger.Where(e => e.StokvelId == id).SumAsync(e => e.Amount, ct),
        entries = rows.Select(r => new { r.Id, r.Member, label = t.T("ledger_" + r.Type.ToString().ToLower(), l), r.Amount, r.CycleNumber, r.CreatedAt, r.Hash })
    });
}).RequireAuthorization();

// ---------- Offline sync: client queues ops locally (each with its own GUID) and flushes when online ----------
app.MapPost("/api/sync", async (SyncDto d, ClaimsPrincipal p, ContributionService cs, CancellationToken ct) =>
{
    var uid = Uid(p); var results = new List<object>();
    foreach (var op in d.Operations)
    {
        if (op.Type != "contribute") { results.Add(new { op.OpId, status = "unsupported" }); continue; }
        var r = await cs.InitiateAsync(uid, op.StokvelId, op.OpId, Channel.Offline, ct);
        results.Add(new { op.OpId, status = r.code });
    }
    return Results.Ok(results);
}).RequireAuthorization();

// ---------- Provider + SMS webhooks (shared-secret; replace with provider HMAC in production) ----------
app.MapPost("/api/payments/webhook", async (HttpRequest http, WebhookDto d, ContributionService cs, CancellationToken ct) =>
    !SecretEquals(http.Headers["X-Signature"].ToString(), cfg["Payments:WebhookSecret"])
        ? Results.Unauthorized()
        : Results.Ok(await cs.ConfirmAsync(d.ProviderRef, d.Status == "success", ct)));

app.MapPost("/api/sms/inbound", async (HttpRequest http, InboundSmsDto d, SmsCommandProcessor proc, CancellationToken ct) =>
{
    if (!SecretEquals(http.Headers["X-Sms-Key"].ToString(), cfg["Sms:InboundKey"])) return Results.Unauthorized();
    await proc.HandleAsync(d.From, d.Text, ct);
    return Results.Ok();
});

// ---------- UI support: list my stokvels, dashboard overview, sandbox payment approval ----------
app.MapGet("/api/stokvels/mine", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
{
    var uid = Uid(p);
    var rows = await (from m in db.Memberships.Where(m => m.UserId == uid && m.Active)
                      join s in db.Stokvels on m.StokvelId equals s.Id
                      select new { s.Id, s.Name, s.ContributionAmount, s.Frequency, m.Role }).ToListAsync(ct);
    return Results.Ok(rows.Select(r => new { r.Id, r.Name, r.ContributionAmount, Frequency = r.Frequency.ToString(), Role = r.Role.ToString() }));
}).RequireAuthorization();

app.MapGet("/api/stokvels/{id:guid}/overview", async (Guid id, ClaimsPrincipal p, AppDbContext db, ContributionService cs, IWebHostEnvironment env, CancellationToken ct) =>
{
    var uid = Uid(p);
    var me = await Mem(db, id, uid);
    if (me is null) return Results.Forbid();
    var s = (await db.Stokvels.FindAsync([id], ct))!;
    var cycle = await cs.CurrentCycleAsync(id, ct);
    var st = await db.Contributions.Where(c => c.StokvelId == id && c.CycleNumber == cycle && c.Status != PaymentStatus.Failed)
        .Select(c => new { c.Id, c.UserId, c.Status }).ToListAsync(ct);
    var members = await (from m in db.Memberships.Where(m => m.StokvelId == id && m.Active)
                         join u in db.Users on m.UserId equals u.Id
                         select new { u.Id, u.FullName, m.Role }).ToListAsync(ct);
    return Results.Ok(new
    {
        s.Id, s.Name, s.ContributionAmount, Frequency = s.Frequency.ToString(), Role = me.Role.ToString(),
        JoinCode = IsAdmin(me) ? s.JoinCode : null, Cycle = cycle, Sandbox = env.IsDevelopment(),
        MyPendingId = st.FirstOrDefault(x => x.UserId == uid && x.Status == PaymentStatus.Pending)?.Id,
        Members = members.Select(m => new
        {
            m.Id, Name = m.FullName, Role = m.Role.ToString(),
            Paid = st.Any(x => x.UserId == m.Id && x.Status == PaymentStatus.Confirmed),
            Pending = st.Any(x => x.UserId == m.Id && x.Status == PaymentStatus.Pending)
        })
    });
}).RequireAuthorization();

if (app.Environment.IsDevelopment())   // stands in for the mobile-money provider's webhook while using the sandbox
    app.MapPost("/api/dev/contributions/{id:guid}/confirm", async (Guid id, ClaimsPrincipal p, AppDbContext db, ContributionService cs, CancellationToken ct) =>
    {
        var uid = Uid(p);
        var c = await db.Contributions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct);
        return c?.ProviderRef is null ? Results.NotFound() : Results.Ok(await cs.ConfirmAsync(c.ProviderRef, true, ct));
    }).RequireAuthorization();

app.Run();

record RegisterDto(string Phone, string FullName, string Pin, string? Language);
record LoginDto(string Phone, string Pin);
record CreateStokvelDto(string Name, decimal ContributionAmount, Frequency Frequency, DateTime StartDate);
record JoinDto(string JoinCode);
record GenerateRotationDto(List<Guid>? Order);
record ContributeDto(Guid? OpId);
record SyncOp(Guid OpId, string Type, Guid StokvelId, DateTime ClientTimestamp);
record SyncDto(List<SyncOp> Operations);
record WebhookDto(string ProviderRef, string Status);
record InboundSmsDto(string From, string Text);
