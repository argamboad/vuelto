using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Perezosoft.Api.Services;

namespace Perezosoft.Api.Tests;

/// <summary>
/// Unit coverage for the <see cref="HttpCurrentTenant"/> tenant-context primitive (ADR-003): an
/// entered tenant overrides the (absent or present) JWT claim, restores cleanly, nests, and refuses
/// the empty tenant. The structural effect on real reads/writes is in <c>EnterTenantScopingTests</c>.
/// </summary>
public class HttpCurrentTenantTests
{
    [Fact]
    public void NoJwt_NoEntered_TenantIsNull()
    {
        var current = new HttpCurrentTenant(new HttpContextAccessor());
        Assert.Null(current.TenantId);
    }

    [Fact]
    public void EnterTenant_OverridesSource_AndRestoresOnDispose()
    {
        var current = new HttpCurrentTenant(new HttpContextAccessor()); // no context → source null
        var tenant = Guid.CreateVersion7();

        using (current.EnterTenant(tenant))
            Assert.Equal(tenant, current.TenantId);

        Assert.Null(current.TenantId);
    }

    [Fact]
    public void EnterTenant_TakesPrecedenceOverJwtClaim()
    {
        var jwtTenant = Guid.CreateVersion7();
        var entered = Guid.CreateVersion7();
        var current = new HttpCurrentTenant(new HttpContextAccessor { HttpContext = ContextWithTenant(jwtTenant) });

        Assert.Equal(jwtTenant, current.TenantId);
        using (current.EnterTenant(entered))
            Assert.Equal(entered, current.TenantId);
        Assert.Equal(jwtTenant, current.TenantId);
    }

    [Fact]
    public void EnterTenant_Nests()
    {
        var current = new HttpCurrentTenant(new HttpContextAccessor());
        var outer = Guid.CreateVersion7();
        var inner = Guid.CreateVersion7();

        using (current.EnterTenant(outer))
        {
            using (current.EnterTenant(inner))
                Assert.Equal(inner, current.TenantId);
            Assert.Equal(outer, current.TenantId);
        }

        Assert.Null(current.TenantId);
    }

    [Fact]
    public void EnterTenant_RejectsEmptyTenant()
    {
        var current = new HttpCurrentTenant(new HttpContextAccessor());
        Assert.Throws<ArgumentException>(() => current.EnterTenant(Guid.Empty));
    }

    private static DefaultHttpContext ContextWithTenant(Guid tenantId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtClaims.TenantId, tenantId.ToString())], "test")),
    };
}
