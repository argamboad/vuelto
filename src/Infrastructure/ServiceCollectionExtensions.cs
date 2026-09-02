using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Audit;
using Perezosoft.Infrastructure.Billing;
using Perezosoft.Infrastructure.Email;
using Perezosoft.Infrastructure.Files;
using Perezosoft.Infrastructure.Http;
using Perezosoft.Infrastructure.Inbox;
using Perezosoft.Infrastructure.Outbox;
using Perezosoft.Infrastructure.Scheduling;
using Perezosoft.Infrastructure.Persistence;
using Perezosoft.Infrastructure.Repositories;
using Perezosoft.Infrastructure.Webhooks;

namespace Perezosoft.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Dedicated cookie scheme that serves only as the temporary carrier for the
    /// external OAuth principal. The provider handlers sign into this scheme; the
    /// callback reads the external principal from it, then issues the real session
    /// (JWT + refresh cookie) and signs the carrier out.
    /// </summary>
    public const string ExternalScheme = "External";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Persistence
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
        // A SCOPED context factory for services that must write OUT-OF-BAND of the ambient
        // transaction — the webhook delivery recorder persists failed-attempt rows through it, so
        // they survive the OutboxProcessor's rollback (HOOKS-2). Scoped (not the default
        // singleton) because AppDbContext takes the scoped ICurrentTenant in its constructor.
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")),
            ServiceLifetime.Scoped);

        // Data Protection — keys stored in DB so the OAuth correlation/nonce cookies
        // survive server restarts/redeploys.
        // The application name is part of key derivation (like the CreateProtector purpose
        // strings): renaming it makes everything already protected — MFA secrets, webhook
        // secrets, in-flight tokens — undecryptable. Kept through the Perezosoft rename;
        // change only alongside a deliberate re-encryption migration.
        services.AddDataProtection()
            .PersistKeysToDbContext<AppDbContext>()
            .SetApplicationName("template");

        // Email — dev: points to Mailpit via appsettings.Development.json
        services.Configure<SmtpSettings>(configuration.GetSection("Email:Smtp"));
        // The real SMTP sender, registered KEYED so the outbox handler can resolve it without
        // getting the outbox decorator (the default IEmailSender) back.
        services.AddKeyedTransient<IEmailSender, SmtpEmailSender>("smtp");
        // App-facing IEmailSender enqueues onto the outbox instead of sending inline (ADR-007);
        // the dispatcher performs the real send out-of-band, with retry. Existing call sites
        // (passwordless, invitations) are unchanged — they still depend on IEmailSender.
        services.AddScoped<IEmailSender, OutboxEmailSender>();

        // Transactional outbox + background dispatcher (ADR-007). OutboxMessage is staged in the
        // same transaction as the business change; the dispatcher drains it via typed handlers.
        services.AddSingleton(new OutboxOptions());
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<IOutboxHandler>(sp =>
            new EmailOutboxHandler(sp.GetRequiredKeyedService<IEmailSender>("smtp")));
        services.AddHostedService<OutboxDispatcher>();

        // Outbound webhook delivery (HOOKS, ADR-016): the "webhook" outbox handler signs + POSTs each
        // delivery (retry/backoff via the outbox). Always registered — dormant until webhooks are enabled
        // and a subscription exists; the management routes are the config-gated part (Program.cs).
        services.AddScoped<IWebhookSecretProtector, WebhookSecretProtector>();
        services.AddSingleton<IOutboundUrlGuard, OutboundUrlGuard>(); // SSRF guard for tenant-supplied webhook URLs (GAP-2)
        services.AddHttpClient<IWebhookSender, WebhookSender>(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IOutboxHandler, WebhookOutboxHandler>();

        // Cancel a provider subscription out-of-band when a tenant is dissolved (BILLING-7).
        services.AddScoped<IOutboxHandler, Billing.BillingCancelOutboxHandler>();

        // Inbox dedup gate — idempotent inbound (webhook) deliveries (ADR-007). Used inline by the
        // receiving endpoint inside its unit of work; no background service.
        services.AddScoped<IInbox, EfInbox>();

        // Append-only tenant audit log (ADR-008) + its dissolve hook.
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<ITenantDataContributor, AuditDataContributor>();

        // Scheduled/recurring jobs (ADR-007). The host ticks and runs each IScheduledJob on its own
        // interval; add a job by registering IScheduledJob (no host edits). Jobs are scoped so they
        // get a fresh DbContext per run.
        services.AddSingleton(new ScheduledJobsOptions());
        services.AddScoped<IScheduledJob, ExpiredTokenCleanupJob>();
        services.AddHostedService<ScheduledJobsHost>();

        // Billing provider (ADR-006, amended by the v2 audit / GAP-1). Stripe when a secret key is
        // configured; otherwise the in-memory fake — but ONLY in Development. The fake trusts a literal
        // webhook signature (FakeBillingProvider), and the webhook endpoint is anonymous, so registering
        // it outside Development would accept forged, unauthenticated cross-tenant billing writes. Fail
        // fast at startup instead, so a misconfigured production deploy cannot boot with the fake.
        services.Configure<StripeSettings>(configuration.GetSection("Billing:Stripe"));
        var stripeKey = configuration["Billing:Stripe:SecretKey"];
        if (!string.IsNullOrEmpty(stripeKey))
        {
            // Mode sanity (v3 audit DEP-10): a presence-only check let a real prod deploy boot silently in
            // TEST mode with a test key, or a LIVE key make real charges on staging. When the deploy
            // declares which mode it expects (Billing:Stripe:ExpectLiveKey), the key prefix must match —
            // fail closed at startup otherwise. Unset ⇒ no mode check (local/dev convenience).
            if (configuration.GetValue<bool?>("Billing:Stripe:ExpectLiveKey") is { } expectLive)
            {
                var isLive = stripeKey.StartsWith("sk_live_", StringComparison.Ordinal);
                var isTest = stripeKey.StartsWith("sk_test_", StringComparison.Ordinal);
                if (expectLive && !isLive)
                    throw new InvalidOperationException(
                        "Billing:Stripe:ExpectLiveKey is true but Billing:Stripe:SecretKey is not a live key "
                        + "(sk_live_…). Refusing to run a live deployment against Stripe test mode.");
                if (!expectLive && !isTest)
                    throw new InvalidOperationException(
                        "Billing:Stripe:ExpectLiveKey is false but Billing:Stripe:SecretKey is not a test key "
                        + "(sk_test_…). Refusing to run a non-live deployment against a live key that can make real charges.");
            }
            services.AddScoped<IBillingProvider, StripeBillingProvider>();
        }
        else if (environment.IsDevelopment())
            services.AddScoped<IBillingProvider, FakeBillingProvider>();
        else
            throw new InvalidOperationException(
                "No Billing:Stripe:SecretKey is configured and the environment is not Development. The " +
                "in-memory FakeBillingProvider trusts a literal webhook signature and must never run " +
                "outside Development (it would accept forged, unauthenticated cross-tenant billing " +
                "writes). Configure a real Stripe secret key for this environment.");

        // File/blob storage (ADR-010). An S3-compatible backend (AWS/MinIO/R2/B2) is selected when a
        // bucket is configured; otherwise local disk — the dev/test default with zero setup. Same
        // config-presence switch as the billing provider. Keys are tenant-scoped and validated
        // server-side by the impl. The download tokenizer backs the local-disk /api/files endpoint.
        services.Configure<LocalFileStorageSettings>(configuration.GetSection("Storage:Local"));
        services.Configure<S3StorageSettings>(configuration.GetSection("Storage:S3"));
        services.AddSingleton<IFileDownloadTokenizer, FileDownloadTokenizer>();
        if (!string.IsNullOrEmpty(configuration["Storage:S3:Bucket"]))
        {
            services.AddSingleton(sp => S3FileStorage.CreateClient(sp.GetRequiredService<IOptions<S3StorageSettings>>().Value));
            services.AddScoped<IFileStorage, S3FileStorage>();
        }
        else
        {
            services.AddScoped<IFileStorage, LocalDiskFileStorage>();
        }

        // Clock — repositories/services depend on TimeProvider for testable time. The host
        // (API) also registers it; TryAdd keeps Infrastructure self-contained without conflict.
        services.TryAddSingleton(TimeProvider.System);

        // Repositories + unit of work
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserLoginRepository, UserLoginRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<ILoginTokenRepository, LoginTokenRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantInvitationRepository, TenantInvitationRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        // Generic repository for feature/domain entities (vertical slices). Platform/auth
        // entities use their dedicated repositories above.
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));

        // External OAuth — to add a new provider, append .AddXxx(...) below.
        // Credentials come from config; set them in .env for dev (never commit secrets).
        var auth = services.AddAuthentication();

        // Temporary carrier cookie for the external principal during the OAuth round-trip.
        auth.AddCookie(ExternalScheme, o =>
        {
            o.Cookie.Name = ".app.external";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        });

        // When the provider redirect fails (user denies consent, state mismatch, etc.)
        // send them back to the client error page instead of throwing an unhandled 500.
        var appBase = configuration["Auth:AppBaseUrl"]?.TrimEnd('/') ?? string.Empty;
        var remoteFailureUrl = $"{appBase}/auth-error";
        Task OnRemoteFailure(RemoteFailureContext ctx)
        {
            ctx.Response.Redirect(remoteFailureUrl);
            ctx.HandleResponse();
            return Task.CompletedTask;
        }

        // Only register an OAuth provider when credentials are present.
        // Configure via .env in dev; environment variables in production.
        if (!string.IsNullOrEmpty(configuration["Authentication:Google:ClientId"]))
            auth.AddGoogle(google =>
            {
                google.ClientId = configuration["Authentication:Google:ClientId"]!;
                google.ClientSecret = configuration["Authentication:Google:ClientSecret"]!;
                google.SignInScheme = ExternalScheme;
                google.Events.OnRemoteFailure = OnRemoteFailure;
            });

        if (!string.IsNullOrEmpty(configuration["Authentication:Microsoft:ClientId"]))
            auth.AddMicrosoftAccount(ms =>
            {
                ms.ClientId = configuration["Authentication:Microsoft:ClientId"]!;
                ms.ClientSecret = configuration["Authentication:Microsoft:ClientSecret"]!;
                ms.SignInScheme = ExternalScheme;
                // Pin the tenant authority. "consumers" = personal Microsoft accounts;
                // the bare default routes to the legacy login.live.com endpoint, which
                // rejects the registered redirect URI. Override to "common",
                // "organizations", or a tenant GUID as needed.
                var msTenant = configuration["Authentication:Microsoft:Tenant"] ?? "consumers";
                ms.AuthorizationEndpoint = $"https://login.microsoftonline.com/{msTenant}/oauth2/v2.0/authorize";
                ms.TokenEndpoint = $"https://login.microsoftonline.com/{msTenant}/oauth2/v2.0/token";
                ms.Events.OnRemoteFailure = OnRemoteFailure;
            });

        return services;
    }
}
