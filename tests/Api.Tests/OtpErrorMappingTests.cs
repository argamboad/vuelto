using Perezosoft.Api.Services;

namespace Perezosoft.Api.Tests;

/// <summary>
/// The OTP verify endpoint must not leak whether an email has an OUTSTANDING code: a junk-code
/// verify for an address with an active code (<see cref="OtpStatus.Invalid"/>) and one without
/// (<see cref="OtpStatus.Expired"/>) must return the identical client-facing error (CONF-6). The
/// <see cref="OtpStatus"/> distinction is kept for server-side logging only.
/// </summary>
public class OtpErrorMappingTests
{
    [Fact]
    public void WrongCode_And_NoActiveCode_YieldIdenticalClientError()
    {
        var wrongCode = OtpErrors.ClientCode(OtpStatus.Invalid);   // had an active code, guessed wrong
        var noActiveCode = OtpErrors.ClientCode(OtpStatus.Expired); // no outstanding code at all

        Assert.Equal(wrongCode, noActiveCode);
        Assert.Equal("invalid_code", wrongCode);
    }

    [Fact]
    public void Lockout_KeepsItsOwnCode()
    {
        // Lockout is a deliberate, user-relevant signal (not the enumeration vector CONF-6 flags).
        Assert.Equal("too_many_attempts", OtpErrors.ClientCode(OtpStatus.TooManyAttempts));
    }
}
