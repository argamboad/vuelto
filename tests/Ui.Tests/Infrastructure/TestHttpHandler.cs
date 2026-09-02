using System.Net;
using System.Text;

namespace Perezosoft.Ui.Tests.Infrastructure;

/// <summary>
/// A controllable <see cref="HttpMessageHandler"/> for the client's <see cref="HttpClient"/>: route
/// "METHOD /path" to a canned response, and record every request for assertions. Unmatched requests 404
/// (a component that calls an unstubbed endpoint fails loudly rather than hanging).
/// </summary>
public sealed class TestHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request the components made, in order — assert against these.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    private readonly Dictionary<string, TaskCompletionSource<HttpResponseMessage>> _gated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stub "METHOD /path" (path only, query ignored) to return <paramref name="json"/> with <paramref name="status"/>.</summary>
    public TestHttpHandler On(HttpMethod method, string path, string json = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        _gated.Remove(Key(method, path)); // a later On() replaces a gate — a stale (completed) gate would
                                          // otherwise shadow the new stub and replay its consumed response
        _routes[Key(method, path)] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return this;
    }

    /// <summary>
    /// Stub "METHOD /path" to HANG until the returned action is invoked — for testing concurrent requests
    /// (e.g. a rapid double-click while the first call is still in flight). Every request to this route
    /// awaits the SAME gate.
    /// </summary>
    public Action OnGated(HttpMethod method, string path, string json = "{}")
    {
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        _gated[Key(method, path)] = tcs;
        return () => tcs.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var key = Key(request.Method, request.RequestUri?.AbsolutePath ?? "/");
        if (_gated.TryGetValue(key, out var gate))
            return gate.Task;
        var response = _routes.TryGetValue(key, out var factory)
            ? factory(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"{{\"error\":\"no stub for {key}\"}}", Encoding.UTF8, "application/json"),
            };
        return Task.FromResult(response);
    }

    private static string Key(HttpMethod method, string path) => $"{method} {path}";
}
