using System.Globalization;
using StokvelSync.Api.Domain;
namespace StokvelSync.Api.Services;

/// Draft translations: have native speakers review before launch.
public class Localizer
{
    static Dictionary<Language, string> L(string en, string zu, string xh, string af) =>
        new() { [Language.En] = en, [Language.Zu] = zu, [Language.Xh] = xh, [Language.Af] = af };

    static readonly Dictionary<string, Dictionary<Language, string>> S = new()
    {
        ["contribution_received"] = L("Contribution of R{0} received for {1}. Thank you!",
            "Inkokhelo ye-R{0} yamukelwe ku-{1}. Siyabonga!",
            "Igalelo le-R{0} lamkelwe kwi-{1}. Enkosi!",
            "Bydrae van R{0} ontvang vir {1}. Dankie!"),
        ["payment_prompt"] = L("Approve the R{0} payment on your phone.",
            "Vuma inkokhelo ye-R{0} efonini yakho.",
            "Yamkela intlawulo ye-R{0} efonini yakho.",
            "Keur die R{0}-betaling op jou foon goed."),
        ["balance"] = L("Balance for {0}: R{1}", "Isalela ye-{0}: R{1}", "Ibhalansi ye-{0}: R{1}", "Saldo vir {0}: R{1}"),
        ["next_payout"] = L("Next payout (cycle {0}): {1} on {2}", "Inkokhelo elandelayo ({0}): {1} mhla ka-{2}",
            "Intlawulo elandelayo ({0}): {1} nge-{2}", "Volgende uitbetaling (siklus {0}): {1} op {2}"),
        ["no_payouts"] = L("No upcoming payouts.", "Azikho izinkokhelo ezizayo.", "Akukho ntlawulo izayo.", "Geen uitbetalings voorhande nie."),
        ["not_member"] = L("You are not a member of {0}.", "Awuyena ilungu le-{0}.", "Awulilo ilungu le-{0}.", "Jy is nie 'n lid van {0} nie."),
        ["unregistered"] = L("Number not registered. Sign up in the StokvelSync app.", "Inombolo ayibhaliswanga.", "Inombolo ayibhaliswanga.", "Nommer nie geregistreer nie."),
        ["payment_failed"] = L("Payment could not be started. Try again.", "Inkokhelo ayiqalanga. Zama futhi.", "Intlawulo ayiqalanga. Zama kwakhona.", "Betaling kon nie begin nie. Probeer weer."),
        ["lang_set"] = L("Language set to English.", "Ulimi lusethwe ku-isiZulu.", "Ulwimi lubekwe kwisiXhosa.", "Taal gestel na Afrikaans."),
        ["unknown_command"] = L("Send: BAL <code>, PAY <code>, NEXT <code> or LANG EN|ZU|XH|AF",
            "Thumela: BAL <code>, PAY <code>, NEXT <code> noma LANG EN|ZU|XH|AF",
            "Thumela: BAL <code>, PAY <code>, NEXT <code> okanye LANG EN|ZU|XH|AF",
            "Stuur: BAL <code>, PAY <code>, NEXT <code> of LANG EN|ZU|XH|AF"),
        ["ledger_contribution"] = L("Contribution", "Inkokhelo", "Igalelo", "Bydrae"),
        ["ledger_payout"] = L("Payout", "Ukukhokhelwa kwemali", "Ukhuphiswa kwemali", "Uitbetaling"),
    };

    public string T(string key, Language lang, params object[] args)
    {
        if (!S.TryGetValue(key, out var m)) return key;
        var f = m.TryGetValue(lang, out var x) ? x : m[Language.En];
        return string.Format(CultureInfo.InvariantCulture, f, args);
    }

    public static Language Parse(string? s, Language fallback) => s?.Trim().ToLowerInvariant() switch
    {
        "zu" => Language.Zu, "xh" => Language.Xh, "af" => Language.Af, "en" => Language.En, _ => fallback
    };
}
