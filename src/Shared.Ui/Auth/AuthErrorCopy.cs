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
    /// <summary>
    /// The copy key for a server OTP error: the lockout line for <c>too_many_attempts</c>, the
    /// private-testing line for <c>signup_not_allowed</c> (GATES-2, ADR-027 — the code was correct, the
    /// deployment simply does not admit this address), else the generic incorrect/expired line.
    /// </summary>
    public static string OtpErrorKey(string? errorCode) => errorCode switch
    {
        "too_many_attempts" => "Login_ErrTooManyAttempts",
        "signup_not_allowed" => "Login_ErrSignupNotAllowed",
        _ => "Login_ErrCodeIncorrect",
    };
}
