namespace Perezosoft.Infrastructure.Email;

public class SmtpSettings
{
    public required string Host { get; init; }
    public int Port { get; init; } = 1025;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string FromAddress { get; init; }
    public string FromName { get; init; } = "App";

    /// <summary>Per-send SMTP timeout (seconds) — bounds a hung server instead of stalling the request.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Validate the relay certificate against revocation lists during the TLS handshake.
    /// Keep the secure default everywhere real; macOS dev boxes may need false — Apple's trust
    /// evaluation can return "incomplete certificate revocation check" for relays (e.g. Brevo),
    /// which MailKit treats as fatal.
    /// </summary>
    public bool CheckCertificateRevocation { get; init; } = true;
}
