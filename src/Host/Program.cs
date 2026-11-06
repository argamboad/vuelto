using Vuelto.Host;
using Vuelto.Host.Persistence;
using Perezosoft.Api;
using Perezosoft.Api.Endpoints;

// The host's whole composition (ADR-034): the platform (Perezosoft.Api's PlatformHosting), configured by the app's options
// (AppComposition.ConfigurePlatform: brand, plans, permissions), then the app's half (AppComposition — after, so the app
// can replace a platform service), then the guard. A change belongs in AppComposition, not here.

// Local dev: the repo-root .env (walking up from the working dir); a no-op without one, and skipped under SKIP_DOTENV=1,
// which the test assembly sets so test hosts never read a developer's .env.
LocalDotEnv.Load();

var builder = WebApplication.CreateBuilder(args);
builder.AddPlatform<AppDbContext>(AppComposition.ConfigurePlatform);
builder.Services.AddAppServices(builder.Configuration);

var app = builder.Build();
await app.MapPlatformAsync();
app.MapAppEndpoints();

// Refuse to boot on two endpoints with the same method and pattern: they would start and then answer every request
// to that route with a 500. Last before Run, so every mapping above is in the table.
RouteTableGuard.EnsureUnique(((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints));
app.Run();

/// <summary>The host's entry point, visible to the integration tests (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
