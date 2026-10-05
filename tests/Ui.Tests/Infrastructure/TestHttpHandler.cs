using System.Net;
using System.Text;

namespace Vuelto.Ui.Tests.Infrastructure;

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
    private readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _slow = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stub "METHOD /path" (path only, query ignored) to return <paramref name="json"/> with <paramref name="status"/>.</summary>
    public TestHttpHandler On(HttpMethod method, string path, string json = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        _slow.Remove(Key(method, path));
        _gated.Remove(Key(method, path)); // a later On() replaces a gate — a stale (completed) gate would
                                          // otherwise shadow the new stub and replay its consumed response
        _routes[Key(method, path)] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return this;
    }

    /// <summary>
    /// Stub "METHOD /path" to answer <paramref name="jsons"/> in order, one per request, repeating the last —
    /// for a page that refetches until the server's answer changes. Swapping stubs with a second
    /// <see cref="On"/> mid-test races the page's own timer; a sequence can't be missed however slow the
    /// machine is.
    /// </summary>
    public TestHttpHandler OnSequence(HttpMethod method, string path, params string[] jsons)
    {
        _gated.Remove(Key(method, path));
        var next = 0;
        _routes[Key(method, path)] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsons[Math.Min(Interlocked.Increment(ref next) - 1, jsons.Length - 1)],
                Encoding.UTF8, "application/json"),
        };
        return this;
    }

    /// <summary>
    /// Stub "METHOD /path" to fail the way an unreachable server does — no response at all (DNS, no signal,
    /// a connection dropped while the host cold-starts): the client sees an <see cref="HttpRequestException"/>.
    /// </summary>
    public TestHttpHandler OnUnreachable(HttpMethod method, string path)
    {
        _gated.Remove(Key(method, path));
        _routes[Key(method, path)] = _ => throw new HttpRequestException("No such host is known.");
        return this;
    }

    /// <summary>
    /// Stub "METHOD /path" to answer <paramref name="answers"/> in order — status and body per request, repeating
    /// the last — for a request that is refused once and then accepted (a 401 that a refresh-and-retry cures).
    /// </summary>
    public TestHttpHandler OnSequence(HttpMethod method, string path, params (HttpStatusCode Status, string Json)[] answers)
    {
        _gated.Remove(Key(method, path));
        var next = 0;
        _routes[Key(method, path)] = _ =>
        {
            var (status, json) = answers[Math.Min(Interlocked.Increment(ref next) - 1, answers.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        };
        return this;
    }

    /// <summary>
    /// Stub "METHOD /path" to answer with a redirect: <paramref name="status"/> (302 by default) and a
    /// <c>Location</c> header (v4 audit T56). A handler under test never follows it — this is the server's
    /// answer as the client's own code sees it, which is what a "did we follow / did we refuse" assertion needs.
    /// </summary>
    public TestHttpHandler OnRedirect(HttpMethod method, string path, string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        _gated.Remove(Key(method, path));
        _slow.Remove(Key(method, path));
        _routes[Key(method, path)] = _ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        };
        return this;
    }

    /// <summary>
    /// Stub "METHOD /path" to answer with a non-JSON body — the HTML error page a proxy or a WAF puts in front
    /// of the API. Code that maps a status by the API's own error body must not read this one as ours.
    /// </summary>
    public TestHttpHandler OnHtml(HttpMethod method, string path, string html, HttpStatusCode status)
    {
        _gated.Remove(Key(method, path));
        _slow.Remove(Key(method, path));
        _routes[Key(method, path)] = _ => new HttpResponseMessage(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        return this;
    }

    /// <summary>
    /// Stub "METHOD /path" to answer after <paramref name="delay"/> on <paramref name="time"/> — and to stop
    /// waiting when the request's token is cancelled, the way a real handler does (v4 audit T56).
    /// <see cref="OnGated"/> waits on the token too but never answers by itself; this one is for a server that
    /// is merely slow, so a client timeout shorter than the delay fires and a longer one gets the answer.
    /// </summary>
    public TestHttpHandler OnDelayed(HttpMethod method, string path, TimeSpan delay, TimeProvider time, string json = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        _gated.Remove(Key(method, path));
        _slow[Key(method, path)] = async (_, cancellationToken) =>
        {
            await Task.Delay(delay, time, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
            return gate.Task.WaitAsync(cancellationToken); // a caller's own timeout cancels the wait, as a real handler would
        if (_slow.TryGetValue(key, out var slow))
            return slow(request, cancellationToken);
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
