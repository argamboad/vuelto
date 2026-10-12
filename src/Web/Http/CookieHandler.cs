using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Vuelto.Web.Http;

// Attaches credentials (cookies) to every API request so the auth cookie is sent.
// Required for cross-origin fetch in Blazor WASM — the browser won't include cookies
// on cross-origin requests unless explicitly requested.
public class CookieHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return base.SendAsync(request, cancellationToken);
    }
}
