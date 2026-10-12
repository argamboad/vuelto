using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vuelto.Api.Observability;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests.Observability;

/// <summary>
/// OBS-1 (ADR-008): the per-request log scope carries tenant_id + user_id for an authenticated request,
/// nothing for an anonymous one, and <b>only identifiers</b> — never a bearer token or other secret.
/// Drives the real middleware and captures the scope it opens.
/// </summary>
public class RequestLoggingScopeMiddlewareTests
{
    [Fact]
    public async Task AuthenticatedRequest_OpensScopeWithTenantAndUser()
    {
        var tenant = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7().ToString();
        var recorder = new ScopeRecordingLogger();
        var nextCalled = false;
        var middleware = new RequestLoggingScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, recorder);

        await middleware.InvokeAsync(ContextWith(userId), new TestCurrentTenant { TenantId = tenant });

        Assert.True(nextCalled);
        var scope = Assert.Single(recorder.Scopes);
        Assert.Equal(tenant, scope["tenant_id"]);
        Assert.Equal(userId, scope["user_id"]);
    }

    [Fact]
    public async Task AnonymousRequest_OpensNoScope()
    {
        var recorder = new ScopeRecordingLogger();
        var middleware = new RequestLoggingScopeMiddleware(_ => Task.CompletedTask, recorder);

        await middleware.InvokeAsync(new DefaultHttpContext(), new TestCurrentTenant());

        Assert.Empty(recorder.Scopes);
    }

    [Fact]
    public async Task Scope_ContainsOnlyIdentifiers_NeverTheBearerToken()
    {
        var tenant = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7().ToString();
        var context = ContextWith(userId);
        context.Request.Headers.Authorization = "Bearer super-secret-token";
        var recorder = new ScopeRecordingLogger();
        var middleware = new RequestLoggingScopeMiddleware(_ => Task.CompletedTask, recorder);

        await middleware.InvokeAsync(context, new TestCurrentTenant { TenantId = tenant });

        var scope = Assert.Single(recorder.Scopes);
        Assert.Equal(["tenant_id", "user_id"], scope.Keys.OrderBy(k => k));
        Assert.DoesNotContain(scope.Values, v => v.ToString()!.Contains("secret"));
    }

    private static DefaultHttpContext ContextWith(string userId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
    };
}

/// <summary>Captures the dictionary scopes opened via <c>BeginScope</c>.</summary>
internal sealed class ScopeRecordingLogger : ILogger<RequestLoggingScopeMiddleware>
{
    public List<IReadOnlyDictionary<string, object>> Scopes { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object>> pairs)
            Scopes.Add(pairs.ToDictionary(p => p.Key, p => p.Value));
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
