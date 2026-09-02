namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// Maps a server auth error code to the resource key for the message to show. Shared by every
/// sign-in surface (web + native OTP verify) so they can't drift: a lockout
/// (<c>too_many_attempts</c>) reads as its own distinct signal — retrying or re-requesting won't
/// help until the window elapses (CONF-5) — while wrong/expired stays on the deliberately-generic
/// line that the server keeps ambiguous for enumeration safety (CONF-6).
/// </summary>
public static class AuthErrorCopy
{
    /// <summary>The lockout copy for <c>too_many_attempts</c>, else the generic incorrect/expired copy.</summary>
    public static string OtpErrorKey(string? errorCode) =>
        errorCode == "too_many_attempts" ? "Login_ErrTooManyAttempts" : "Login_ErrCodeIncorrect";
}
