namespace StokvelSync.Api.Services;

public record PaymentRequest(string Phone, decimal Amount, string Reference);
public record PaymentInitResult(bool Accepted, string ProviderRef, string? Error);

/// Implement for a real SA provider (MTN MoMo, Vodacom, Ozow, Peach Payments, Paystack ZA...).
/// Initiate a push prompt to the member's phone; the provider calls /api/payments/webhook on completion.
public interface IPaymentProvider
{
    Task<PaymentInitResult> InitiateAsync(PaymentRequest request, CancellationToken ct);
}

public class SandboxPaymentProvider(ILogger<SandboxPaymentProvider> log) : IPaymentProvider
{
    public Task<PaymentInitResult> InitiateAsync(PaymentRequest r, CancellationToken ct)
    {
        log.LogInformation("SANDBOX payment prompt to {Phone}: R{Amount} ref {Ref}", r.Phone, r.Amount, r.Reference);
        return Task.FromResult(new PaymentInitResult(true, "SBX-" + Guid.NewGuid().ToString("N")[..16], null));
    }
}
