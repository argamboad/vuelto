namespace Vuelto.Api.Configuration;

/// <summary>
/// The signup green list (GATES-2, ADR-027): who may create an account, and with it a household of
/// their own. Bound from the <c>Signup</c> config section (env <c>Signup__AllowedEmails__0</c>,
/// <c>Signup__AllowedDomains__0</c>).
/// <para>
/// <b>Empty means open</b>, which is the one gate in this codebase whose unconfigured state is
/// permissive — deliberately. A template whose fresh apps are born locked would be wrong, and the risk
/// the other gates guard against (a feature silently shipping ON) maps here to a non-empty default,
/// which <c>ConfigPostureTests</c> pins against. The startup log states which posture is in effect so a
/// misspelled key cannot quietly leave a private deployment open.
/// </para>
/// Both lists are matched case-insensitively. A domain entry matches the part after the <c>@</c>
/// exactly — it is not a suffix match, so <c>example.com</c> does not admit <c>notexample.com</c>.
/// </summary>
public sealed class SignupSettings
{
    public const string SectionName = "Signup";

    /// <summary>Full addresses allowed to create an account.</summary>
    public string[] AllowedEmails { get; set; } = [];

    /// <summary>Email domains allowed to create an account, without the <c>@</c>.</summary>
    public string[] AllowedDomains { get; set; } = [];

    /// <summary>True when a green list is configured at all. Empty ⇒ signup is open to everyone.</summary>
    public bool IsRestricted => AllowedEmails.Length > 0 || AllowedDomains.Length > 0;

    /// <summary>Whether this address is itself on the list (ignoring any invitation it may hold).</summary>
    public bool Allows(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase)) return true;

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return false;
        return AllowedDomains.Contains(email[(at + 1)..], StringComparer.OrdinalIgnoreCase);
    }
}
