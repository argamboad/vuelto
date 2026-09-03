using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Vuelto.Api.Authentication;
using Vuelto.Api.Configuration;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Features.Budget;
using Vuelto.Api.Features.Catalog;
using Vuelto.Api.Features.Dashboard;
using Vuelto.Api.Features.Email;
using Vuelto.Api.Features.Envelopes;
using Vuelto.Api.Features.Reports;
using Vuelto.Api.Features.ExchangeRate;
using Vuelto.Api.Features.Expenses;
using Vuelto.Api.Features.Ledger;
using Vuelto.Api.Observability;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Infrastructure;
using Vuelto.Infrastructure.ExchangeRate;
using Vuelto.Infrastructure.Mail;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Vouchers;

// Local dev: load secrets/config from the repo-root .env (the single local source of truth —
// see docs/DECISIONS.md). TraversePath walks up to find it regardless of the working dir; the
// try/catch makes it a no-op when there's no .env (e.g. production, which uses real env vars).
try { DotNetEnv.Env.TraversePath().Load(); } catch { /* no .env present */ }

var builder = WebApplication.CreateBuilder(args);

// Structured logging with per-request scopes (OBS-1, ADR-008): readable single-line console in dev,
// JSON in prod so a log aggregator can index the tenant_id/user_id scope. Swap in Serilog/OTel-logs
// later without touching call sites.
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
    builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });
else
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });

builder.Services.AddControllers();

// OpenAPI / Swagger UI. The "Authorize" button takes a JWT access token (get one
// from POST /api/auth/refresh after signing in) so protected endpoints are testable.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Vuelto API", Version = "v1" });
    // A curated "public" document with ONLY the /api/public routes (PUBAPI-2) — the customer-facing
    // contract, served leak-free at /api/public/openapi.json when PUBAPI is enabled (see below).
    o.SwaggerDoc("public", new OpenApiInfo
    {
        Title = "Public API",
        Version = "v1",
        Description = "Programmatic API authenticated with a tenant API key sent in the X-Api-Key header.",
    });
    o.DocInclusionPredicate((docName, api) =>
    {
        var isPublic = api.RelativePath?.StartsWith("api/public", StringComparison.OrdinalIgnoreCase) == true;
        return docName == "public" ? isPublic : true; // "public" = only /api/public; "v1" = everything
    });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste a JWT access token (without the 'Bearer ' prefix)."
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// Infrastructure: DbContext, Data Protection, email, repositories, and the
// External cookie + OAuth provider schemes.
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// OpenTelemetry traces + metrics (OBS-2). Exporter is config-gated (OTLP when configured); see
// TelemetryExtensions. Spans are tagged with tenant_id/user_id.
builder.Services.AddAppTelemetry(builder.Configuration);

// Health checks (OBS-3). /health = liveness (process up); /health/ready = readiness (DB reachable).
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

// Typed settings (read configuration once at startup). Returns the single IJwtSettings instance so
// the JWT-bearer handler below reuses it (no duplicate construction — DEBT-1).
var jwtSettings = builder.Services.AddAppSettings(builder.Configuration);

// Clock — injected so services are testable.
builder.Services.AddSingleton(TimeProvider.System);

// Per-epic service registrations — the DI wiring, grouped by concern (see ServiceRegistrationExtensions).
builder.Services.AddAuthServices();
builder.Services.AddTenantServices();
builder.Services.AddMfaServices();
builder.Services.AddNotificationServices();
builder.Services.AddPlatformAdminServices(builder.Configuration);
builder.Services.AddRbacServices();
builder.Services.AddBillingServices();

// App feature slices (src/Api/Features/<Feature>) register their handler + ITenantDataContributor
// here and map their group below — only Program.cs may reference Features.* (R8).
builder.Services.AddScoped<BudgetSettingsHandler>();                                   // BUDGET-1
builder.Services.AddScoped<ITenantDataContributor, BudgetSettingsDataContributor>();
builder.Services.AddScoped<CategoryCatalogHandler>();                                  // CATALOG-1/2
builder.Services.AddScoped<BankCatalogHandler>();
builder.Services.AddScoped<ITenantDataContributor, CategoryDataContributor>();
builder.Services.AddScoped<ITenantDataContributor, BankDataContributor>();
builder.Services.AddExchangeRates(builder.Configuration);                             // FX-1 (no entity)
builder.Services.AddVoucherParsing();                                                 // EMAIL-1 (pure parser library; no entity)
builder.Services.AddMailIngestion(builder.Configuration);                             // EMAIL-2/3 (token protector, consent, Graph + Gmail readers)
builder.Services.AddScoped<IExchangeRateResolver, ExchangeRateResolver>();
builder.Services.AddScoped<IRecentRateSource, TransactionRecentRateSource>();          // LEDGER-2 fills the chain's last tier
builder.Services.AddScoped<EnvelopeHandler>();                                         // ENV-1
builder.Services.AddScoped<ITenantDataContributor, EnvelopeDataContributor>();
builder.Services.AddSingleton<IWeekBoundaryService, WeekBoundaryService>();            // pure Core service (BUDGET-1)
builder.Services.AddScoped<MonthHandler>();                                            // LEDGER-1/2
builder.Services.AddScoped<TransactionHandler>();
builder.Services.AddScoped<RefundHandler>();                                           // LEDGER-3
builder.Services.AddScoped<ITenantDataContributor, LedgerDataContributor>();
builder.Services.AddScoped<FixedExpenseHandler>();                                     // EXPENSES-1
builder.Services.AddScoped<VariableExpenseHandler>();
builder.Services.AddScoped<ITenantDataContributor, FixedExpenseDataContributor>();
builder.Services.AddScoped<ITenantDataContributor, VariableExpenseDataContributor>();
builder.Services.AddSingleton<IDashboardSummaryService, DashboardSummaryService>();    // DASH-1 (pure Core calc)
builder.Services.AddScoped<DashboardHandler>();
builder.Services.AddScoped<ReportHandler>();                                            // REPORTS-1/2
builder.Services.AddScoped<EmailConnectionHandler>();                                   // EMAIL-2 (user-keyed, ADR-V002)
builder.Services.AddScoped<IUserDataContributor, EmailConnectionUserDataContributor>();

// Caches + session (LinkTokenService uses IMemoryCache; session backed by distributed cache).
builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

// JWT Bearer — authenticates /api/* endpoints with the app-issued access token.
// Validation mirrors JwtTokenService (issuer = audience = Jwt:Issuer). Reuses the SAME
// IJwtSettings instance registered as the DI singleton above (AddAppSettings) — one source of truth.
var authBuilder = builder.Services.AddAuthentication()
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        // Single validation definition shared with JwtTokenService — see JwtValidation.
        options.TokenValidationParameters = jwtSettings.CreateParameters();
    });

// PUBAPI (ADR-015): the public API + API keys. Default OFF — a deployment opts in via PublicApi:Enabled.
// Strong gating: the API-key scheme is only added, and the routes only mapped (below), when enabled.
var publicApiSettings = new PublicApiSettings();
builder.Configuration.GetSection(PublicApiSettings.SectionName).Bind(publicApiSettings);
builder.Services.AddSingleton(publicApiSettings);
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
// API keys participate in tenant dissolve + export (LB-TEN-1) regardless of the PublicApi toggle — keys may
// linger from when it was enabled, so a dissolved tenant must never orphan hashed key credentials.
builder.Services.AddScoped<ITenantDataContributor, ApiKeyDataContributor>();
if (publicApiSettings.Enabled)
    authBuilder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

// HOOKS (ADR-016): outbound webhooks. Default OFF — a deployment opts in via Webhooks:Enabled. The
// delivery machinery is always registered (dormant); only the management routes (below) are gated.
var webhooksSettings = new WebhooksSettings();
builder.Configuration.GetSection(WebhooksSettings.SectionName).Bind(webhooksSettings);
builder.Services.AddSingleton(webhooksSettings);
builder.Services.AddScoped<IWebhookSubscriptionService, WebhookSubscriptionService>();
builder.Services.AddScoped<IWebhookPublisher, WebhookPublisher>();
// Webhooks participate in tenant dissolve + export (LB-TEN-1) regardless of the Webhooks toggle — a
// dissolved tenant must never orphan its encrypted signing secret or delivery logs.
builder.Services.AddScoped<ITenantDataContributor, WebhookDataContributor>();

// Single tenant-API authorization policy, shared by the platform controllers and feature groups.
builder.Services.AddTenantApiAuthorization();

// Throttle the unauthenticated passwordless endpoints (email-bomb / brute-force surface) — CONF-5.
builder.Services.AddApiRateLimiters(builder.Configuration);

// Reverse-proxy correctness (DEPLOY-1, ADR-017), config-gated off. Behind Render/nginx, honor the
// proxy's X-Forwarded-For/-Proto so the rate limiter sees the real client IP and OAuth URIs use https.
builder.Services.AddProxyForwarding(builder.Configuration);

// CORS — allow the Blazor WASM client to send credentialed requests (cookies).
var allowedOrigins = builder.Configuration
    .GetSection("Auth:AllowedOrigins")
    .Get<string[]>() ?? [];

if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddPolicy("BlazorClient", p => p
        .WithOrigins(allowedOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()));
}

var app = builder.Build();

// Apply EF migrations on startup so a freshly-created database (e.g. after a
// `docker compose down -v && up`) gets its schema with no manual `dotnet ef database update`.
// QA_TEST_PLAN documents "re-apply migrations by starting the API" — this is what does it.
// Migrate() is idempotent (a no-op when already current); the relational guard keeps
// non-relational test providers unaffected. AppDbContext needs ICurrentTenant, which resolves
// to a null tenant outside a request — fine, migrating doesn't use the tenant filter.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
    {
        // Two-role topology (ADR-020): when the app runs as the RLS-subject runtime role, DDL —
        // including EF's own history-table bootstrap — needs the owner/migrator connection.
        // ConnectionStrings:Migrations is optional; absent (single-role setups: local dev, current
        // staging) migrations run over the default connection as before. The override only affects
        // this scoped context, which exists solely to migrate.
        var migrationsConnection = app.Configuration.GetConnectionString("Migrations");
        if (!string.IsNullOrEmpty(migrationsConnection))
            db.Database.SetConnectionString(migrationsConnection);
        db.Database.Migrate();
    }
}

// RLS posture guard (ADR-020, config-gated; prod activation enables it): refuse to start if the
// runtime connection would silently bypass row-level security. Fresh scope — the migrate scope's
// context may have been repointed at the migrator connection above.
if (builder.Configuration.GetValue<bool>(RlsPostureGuard.EnforceConfigKey))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
        await RlsPostureGuard.EnsureRuntimeRoleIsNotPrivilegedAsync(db);
}

// Forwarded headers must run FIRST — before HTTPS redirect, auth, or the rate limiter read the
// IP/scheme. No-op unless Proxy:Enabled (see AddProxyForwarding).
app.UseProxyForwarding(app.Configuration);

// Single-origin hosting (DEPLOY-1, ADR-017), config-gated off. When enabled, the API also serves the
// published Blazor WASM client so the whole app is one origin (first-party refresh cookie, no CORS).
// Default off — local dev uses the separate `src/Web` dev server; the deployed container sets this true
// and ships the published wwwroot alongside the API. Static assets are served before auth.
var serveWebClient = app.Configuration.GetValue("Hosting:ServeWebClient", false);
if (serveWebClient)
{
    // The API became a browser HTML host in DEPLOY-1 but shipped with zero security/cache headers
    // (v3 audit DEP-2/DEP-3). Add them for every response served from this origin:
    //  • security headers — nosniff, clickjacking (frame-ancestors + X-Frame-Options), Referrer-Policy;
    //  • cache policy — fingerprinted /_framework assets cache immutably; the SPA shell (and other
    //    client routes) is `no-cache` so a post-deploy Blazor integrity mismatch can't pin a stale shell.
    // HSTS is added separately below (production only — Development talks cleartext to the Android emulator).
    app.Use(async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        var path = ctx.Request.Path;
        ctx.Response.OnStarting(() =>
        {
            if (path.StartsWithSegments("/_framework"))
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            else if (!path.StartsWithSegments("/api"))
                ctx.Response.Headers.CacheControl = "no-cache";
            return Task.CompletedTask;
        });
        await next();
    });

    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Vuelto API v1");
        if (publicApiSettings.Enabled)
            c.SwaggerEndpoint("/swagger/public/swagger.json", "Public API"); // PUBAPI-2
        c.RoutePrefix = string.Empty; // serve the UI at the API root (/)
    });
}

// HTTPS redirect is a production concern. In Development we deliberately skip it so the
// Android emulator can talk cleartext HTTP to the host (http://10.0.2.2:5238) without the
// request being 307'd to a port/cert it can't reach. Native auth uses body tokens (no
// cookies), so none of the web client's HTTPS/SameSite requirements apply to that leg.
if (!app.Environment.IsDevelopment())
{
    // HSTS pairs with the HTTPS redirect (DEP-2): tell browsers to stick to HTTPS. Production only —
    // Development skips it for the same cleartext-emulator reason as the redirect.
    if (serveWebClient)
        app.UseHsts();
    app.UseHttpsRedirection();
}

if (allowedOrigins.Length > 0)
    app.UseCors("BlazorClient");

app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
// Enrich every log line with tenant_id/user_id once the principal is resolved (OBS-1).
app.UseRequestLoggingScope();
app.UseRateLimiter();
app.MapControllers();

// Health/readiness (OBS-3). Unauthenticated, status-only (the default writer emits just the status,
// so no internals leak). Liveness runs no checks; readiness runs the "ready"-tagged DB check.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// Deployed build identity (DEPLOY-3). Anonymous; returns the commit this instance is running, from the
// platform's env (Render sets RENDER_GIT_COMMIT) or an explicit APP_BUILD_COMMIT, else "unknown". The
// post-deploy smoke polls this to wait for the NEW build to actually be live before asserting — the old
// instance keeps serving during a build, so health alone can't tell old from new.
app.MapGet("/api/version", () => Results.Ok(new
{
    commit = Environment.GetEnvironmentVariable("APP_BUILD_COMMIT")
             ?? Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
             ?? "unknown",
})).AllowAnonymous().WithTags("Platform");

// App feature slice endpoints are mapped here (app.Map<Feature>()), one call per slice.
app.MapBudgetSettings(); // BUDGET-1
app.MapCatalog();        // CATALOG-1/2 (/api/categories, /api/banks)
app.MapExchangeRate();   // FX-1
app.MapEnvelopes();      // ENV-1
app.MapLedger();         // LEDGER-1/2/3 (/api/months, /api/transactions, /api/refunds)
app.MapExpenses();       // EXPENSES-1 (/api/expenses/fixed, /api/expenses/variable)
app.MapDashboard();      // DASH-1 (/api/months/{id}/summary)
app.MapReports();        // REPORTS-1/2 (/api/reports/category-analysis, /api/reports/transactions/export)
app.MapEmail();          // EMAIL-2/3 (/api/email/connections — user-scoped; the consent callback is the one anonymous route)
// Billing is a platform controller (BillingController) — auto-mapped by MapControllers above.

// PUBAPI (ADR-015): map key management + the public routes only when enabled — off ⇒ they don't exist.
if (publicApiSettings.Enabled)
{
    app.MapApiKeyManagement();
    app.MapPublicApi();

    // The customer-facing OpenAPI contract (PUBAPI-2): emit ONLY the curated "public" document, so the
    // internal "v1" surface is never exposed in production. Anonymous (a published contract), any env.
    app.MapGet("/api/public/openapi.json", (Swashbuckle.AspNetCore.Swagger.ISwaggerProvider swagger) =>
    {
        var document = swagger.GetSwagger("public");
        using var writer = new StringWriter();
        document.SerializeAsV3(new Microsoft.OpenApi.Writers.OpenApiJsonWriter(writer));
        return Results.Text(writer.ToString(), "application/json");
    }).AllowAnonymous().WithTags("Public API");
}

// HOOKS (ADR-016): map webhook management only when enabled — off ⇒ the routes don't exist.
if (webhooksSettings.Enabled)
    app.MapWebhookManagement();

// Single-origin SPA fallback (DEPLOY-1). An unmatched /api/* must be a real API-shaped 404 — never the
// SPA shell (the more specific fallback out-precedences the catch-all file fallback for /api paths).
// Everything else (client-side routes) falls back to index.html so deep links load the WASM app.
if (serveWebClient)
{
    app.MapFallback("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");
}

app.Run();
