using System.Security.Claims;
using Vuelto.Api.Services;

namespace Vuelto.Api.Tests;

/// <summary>
/// <see cref="ClaimsExtractor.IsEmailVerified"/> is the fail-closed gate feeding the
/// account-takeover guard in <c>UserService.GetOrCreateUserAsync</c>: a new provider whose
/// email matches an existing account may only be merged when the provider has PROVEN the
/// address. An absent or non-affirmative <c>email_verified</c> claim must read as NOT verified
/// (MITI-3) — otherwise a provider that simply omits the claim (e.g. some Microsoft configs)
/// would silently bypass the guard.
/// </summary>
public class ClaimsExtractorTests
{
    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    [Fact]
    public void EmailVerified_AbsentClaim_IsNotVerified()
    {
        // No email_verified claim at all — must fail closed.
        var principal = PrincipalWith(new Claim(ClaimTypes.Email, "x@example.com"));
        Assert.False(new ClaimsExtractor().IsEmailVerified(principal));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("", false)]
    [InlineData("1", false)]   // only an explicit "true" counts
    [InlineData("yes", false)]
    public void EmailVerified_ReflectsClaimValue(string value, bool expected)
    {
        var principal = PrincipalWith(new Claim("email_verified", value));
        Assert.Equal(expected, new ClaimsExtractor().IsEmailVerified(principal));
    }
}
