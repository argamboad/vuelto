using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OtpNet;
using Vuelto.Api.Services;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure;
using Vuelto.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// v2 audit B8-6: the HTTP integration harness. Boots the REAL app (<c>Program</c>) via
/// <see cref="WebApplicationFactory{T}"/> against a throwaway Postgres container — so tests exercise the
/// full pipeline (JWT-bearer auth → tenant filter → controllers → EF → Postgres), not hand-assembled
/// services. This is what lets the MFA step-up (B8-2) and controller-split (B9-1) work be proven at the
/// wire, and de-risks routing/DI refactors by asserting routes stay stable end-to-end.
///
/// The app boots in Development (so the fake billing provider is allowed — B1) and runs its real
/// startup migrations against the fresh container, exercising the migration path too.
///
/// Two seams are needed because <c>Program</c> reads config <b>before</b> <c>builder.Build()</c> (so the
/// factory's Build-time config hooks are too late): (1) <c>Jwt:Secret</c> is supplied as an environment
/// variable so it exists at CreateBuilder time — its value is irrelevant, since minting and validation
/// both resolve the app's own settings; (2) the <c>AppDbContext</c> registration is replaced in
/// <c>ConfigureTestServices</c> to point at the throwaway container, which is env-independent and (unlike
/// a config override) can't be defeated by a repo <c>.env</c>, so the container is always the DB used.
/// </summary>
public sealed class IntegrationTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Long enough for HMAC-SHA256 (JwtSettings requires ≥32 chars); value is irrelevant, only length + validity.
    private const string TestJwtSecret = "integration-test-jwt-secret-key-0123456789";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    /// <summary>Superuser connection string of the throwaway container (for catalog-level asserts).</summary>
    public string DatabaseConnectionString => _container.GetConnectionString();

    public IntegrationTestFactory()
    {
        // Must be present when Program reads Jwt:Secret at CreateBuilder time (before Build) — env vars
        // are a CreateBuilder config source; the WebApplicationFactory config hooks run too late.
        Environment.SetEnvironmentVariable("Jwt__Secret", TestJwtSecret);
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync(); // must be up before the host is first built (Program migrates on boot)

        // RLS backstop (ADR-020): the container's default user is a superuser, which Postgres
        // exempts from RLS — so the harness would silently skip the DB-level tenancy wall. Instead
        // the app is pointed at a non-privileged runtime role (like staging/prod on Neon): migrate
        // as the superuser first, provision the role + grants, and hand the app the runtime
        // connection (Program's startup Migrate() is then an idempotent no-op needing only SELECT
        // on the history table). Every HTTP integration test therefore runs with RLS ENFORCED.
        var superuserOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        await using var db = new AppDbContext(superuserOptions, new TestCurrentTenant());
        await db.Database.MigrateAsync();
        // Role + grants ONLY — never the model-derived RLS policies. The migrations just applied the real
        // policies; back-filling them from the model here would make the RLS parity gate tautological
        // (v3 audit RLS-1). See RlsTestSetup.ProvisionRuntimeRoleAsync.
        await Rls.RlsTestSetup.ProvisionRuntimeRoleAsync(db);
    }

    /// <summary>Connection string the app under test uses: the RLS-subject runtime role.</summary>
    private string RuntimeConnectionString =>
        Rls.RlsTestSetup.RuntimeConnectionString(_container.GetConnectionString());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();          // tears down the WebApplicationFactory host
        await _container.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // EF's startup Migrate() bootstraps its history table with DDL the runtime role must not be
        // allowed to run — route migrations over the superuser, exactly like the two-role prod
        // topology (ConnectionStrings:Migrations, read after Build so this hook is early enough).
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            [new("ConnectionStrings:Migrations", _container.GetConnectionString())]));
        builder.ConfigureTestServices(services =>
        {
            // Repoint the DbContext at the throwaway container — bulletproof regardless of config/.env.
            // As the RUNTIME role, not the superuser, so RLS (ADR-020) is enforced across the suite.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(RuntimeConnectionString));

            // Swap the External (OAuth carrier) cookie scheme's handler for a test one that authenticates
            // from headers — so the OAuth callback's [Authorize(External)] can be driven without a real
            // provider round-trip. The scheme's cookie options are left intact (the handler ignores them).
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                if (o.SchemeMap.TryGetValue(ServiceCollectionExtensions.ExternalScheme, out var scheme))
                    scheme.HandlerType = typeof(TestExternalAuthHandler);
            });

            // Give every request a unique source IP. The passwordless rate limiter partitions on
            // RemoteIpAddress (null in TestServer → one shared bucket), which would otherwise trip 429
            // across the auth-flow tests. This keeps the limiter active but non-colliding — the limiter
            // itself is tested in isolation by RateLimitingTests.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, UniqueClientIpStartupFilter>();
        });
    }

    /// <summary>Seeds a tenant + owner and enrolls+enables real TOTP MFA; returns the plaintext secret.</summary>
    public async Task<(SeededUser User, string Secret)> SeedMfaUserAsync(string? email = null)
    {
        var user = await SeedUserAsync(TenantRoles.Owner, email);
        using var scope = Services.CreateScope();
        var mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
        var enrollment = await mfa.BeginEnrollmentAsync(user.UserId)
            ?? throw new InvalidOperationException("MFA enrollment failed to start.");
        var (result, _) = await mfa.ConfirmEnrollmentAsync(user.UserId, Totp(enrollment.Secret));
        if (result != MfaConfirmResult.Enabled)
            throw new InvalidOperationException($"MFA enrollment did not enable: {result}.");
        return (user, enrollment.Secret);
    }

    /// <summary>Computes the current TOTP for a Base32 secret (same library the app uses).</summary>
    public static string Totp(string base32Secret) => new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    /// <summary>A client that does NOT auto-follow redirects, so a 302 Location can be inspected.</summary>
    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Seeds a tenant + a user who owns it, and returns the identifiers a token needs.</summary>
    public async Task<SeededUser> SeedUserAsync(string role = TenantRoles.Owner, string? email = null)
    {
        email ??= $"user-{Guid.CreateVersion7():N}@test.local";
        var tenant = new Tenant { Name = "Test Household", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var user = new User { Email = email, DisplayName = "Test User", EmailVerified = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var membership = new TenantMembership { TenantId = tenant.Id, UserId = user.Id, Role = role, JoinedAt = DateTimeOffset.UtcNow };

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Set<Tenant>().Add(tenant);
        db.Set<User>().Add(user);
        db.Set<TenantMembership>().Add(membership);
        await db.SaveChangesAsync();

        return new SeededUser(user.Id, tenant.Id, email, role);
    }

    /// <summary>Mints a real access token for a seeded user via the app's own token service.</summary>
    public string IssueAccessToken(SeededUser user)
    {
        using var scope = Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return tokens.IssueAccessToken(user.UserId, user.Email, provider: "test", tenantId: user.TenantId);
    }

    /// <summary>An HttpClient with a valid bearer token for the given seeded user.</summary>
    public HttpClient CreateClientFor(SeededUser user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", IssueAccessToken(user));
        return client;
    }

    /// <summary>
    /// An HttpClient carrying a REAL impersonation token — <paramref name="staff"/> "signed in as"
    /// <paramref name="target"/>, minted by the app's own token service exactly like ADMIN-2 does. For
    /// the impersonation gate/attribution tests (v3 ADM-2 / LB-ADM-1).
    /// </summary>
    public HttpClient CreateImpersonatingClientFor(SeededUser staff, SeededUser target)
    {
        using var scope = Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var token = tokens.IssueImpersonationToken(
            target.UserId, target.Email, staff.UserId, TimeSpan.FromMinutes(15),
            displayName: "Impersonated", tenantName: "Test Household", tenantId: target.TenantId);

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>Identifiers for a seeded user, enough to mint a token and assert tenant scoping.</summary>
public sealed record SeededUser(Guid UserId, Guid TenantId, string Email, string Role);

/// <summary>
/// Assigns each request a unique loopback-range source IP before the app pipeline runs, so the
/// IP-partitioned passwordless rate limiter never collides across integration tests (see the harness
/// comment). Deterministic (counter-based) so runs are reproducible.
/// </summary>
internal sealed class UniqueClientIpStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    private int _counter;

    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (ctx, nextMw) =>
        {
            var i = System.Threading.Interlocked.Increment(ref _counter);
            ctx.Connection.RemoteIpAddress = new System.Net.IPAddress([10, (byte)(i >> 16), (byte)(i >> 8), (byte)i]);
            await nextMw();
        });
        next(app);
    };
}

/// <summary>
/// Test stand-in for the External OAuth carrier scheme: authenticates from the <c>X-Test-Provider-User</c>
/// and <c>X-Test-Email</c> headers so an integration test can drive the OAuth callback (which is
/// <c>[Authorize(External)]</c>) without a real provider round-trip. Extends the cookie handler so the
/// scheme's existing <see cref="Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions"/>
/// still resolve; the header logic replaces the cookie read.
/// </summary>
public sealed class TestExternalAuthHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var providerUserId = Request.Headers["X-Test-Provider-User"].ToString();
        var email = Request.Headers["X-Test-Email"].ToString();
        if (string.IsNullOrEmpty(providerUserId) || string.IsNullOrEmpty(email))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new System.Security.Claims.ClaimsIdentity(
        [
            new(System.Security.Claims.ClaimTypes.NameIdentifier, providerUserId),
            new(System.Security.Claims.ClaimTypes.Email, email),
        ], Scheme.Name);
        var ticket = new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

[CollectionDefinition(IntegrationCollection.Name)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationTestFactory>
{
    public const string Name = "integration";
}
