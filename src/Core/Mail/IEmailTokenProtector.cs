namespace Vuelto.Core.Mail;

/// <summary>
/// Protects the OAuth tokens stored on an <c>EmailConnection</c> at rest (EMAIL-2, ADR-V016). The
/// implementation rides the platform's Data Protection key ring (persisted in the database), so no
/// separate encryption secret exists to provision, rotate or leak. Plaintext never leaves the server.
/// </summary>
public interface IEmailTokenProtector
{
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>; throws if the payload is tampered or from another key ring.</summary>
    string Unprotect(string protectedValue);
}
