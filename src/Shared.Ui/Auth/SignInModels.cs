namespace Vuelto.Shared.Ui.Auth;

/// <summary>Outcome of a native primary-auth attempt: signed in, failed, or owes an MFA step-up.</summary>
public enum SignInStatus { Success, Failed, MfaRequired }

/// <summary>
/// A native sign-in result; carries the MFA challenge when <see cref="SignInStatus.MfaRequired"/>,
/// and on failure the server's error code (e.g. <c>too_many_attempts</c>) so the UI can pick the
/// right copy instead of a blanket "incorrect or expired".
/// </summary>
public sealed record SignInResult(SignInStatus Status, string? Challenge = null, string? Error = null)
{
    public static readonly SignInResult Failed = new(SignInStatus.Failed);
    public static readonly SignInResult Success = new(SignInStatus.Success);
    public static SignInResult FailedWith(string? error) => new(SignInStatus.Failed, Error: error);
    public static SignInResult Mfa(string challenge) => new(SignInStatus.MfaRequired, challenge);
}
