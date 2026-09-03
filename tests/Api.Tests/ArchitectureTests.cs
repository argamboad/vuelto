using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Controllers;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests;

/// <summary>
/// Architecture invariants enforced by the build (B9-1), so the structural guarantees the audit
/// established can't silently regress: feature slices never bypass the tenant filter, every
/// tenant-scoped entity IS filtered, and Blazor components live in the shared RCL — not the web app.
/// These are source/model scans, no extra dependency.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void FeatureSlices_DoNotBypassTheTenantFilter()
    {
        // R5/R38/S0-G2/RLS-6: request-path tenant-scoped code — feature slices AND the config-gated
        // platform surfaces under Endpoints/ (PUBAPI/HOOKS: /api/webhooks, /api/apikeys, /api/public) —
        // must not reach past the tenant filter / RLS backstop. The guard bans IgnoreQueryFilters and the
        // RLS bypass tag (both the RlsTags identifier AND the raw "rls:cross-tenant" literal — RLS-6)
        // outright, and QueryAllTenants except in a *DataContributor.cs (dissolve/export). QueryAllTenants
        // internally applies the tag; writing either directly self-sanctions to the DB-level backstop.
        var sources = SourceFiles(Path.Combine(RepoRoot(), "src", "Api", "Features"))
            .Concat(SourceFiles(Path.Combine(RepoRoot(), "src", "Api", "Endpoints")))
            .Select(f => (f, File.ReadAllText(f)));

        var offenders = Architecture.TenantHatchGuard.FindOffenders(sources);
        Assert.True(offenders.Count == 0,
            "Request-path code (Features/ + Endpoints/) must not bypass the tenant filter — use "
            + $"IRepository<T>.QueryAllTenants() only in a *DataContributor:\n - {string.Join("\n - ", offenders)}");
    }

    [Fact]
    public void QueryAllTenants_IsNotComposedWithSetBasedWrites()
    {
        // R39/RLS-4/RLS-8: QueryAllTenants()'s cross-tenant sanction is a query tag, which EF does not
        // reliably render into ExecuteUpdate/ExecuteDelete (renders for Delete, NOT Update — see
        // RlsBackstopTests — and either can change). Composing them reads as sanctioned but silently affects
        // the wrong rows under the DB RLS policy. Set-based writes must EnterTenant(target) and use Query()/
        // IgnoreQueryFilters instead. Cross-tenant READS via QueryAllTenants() are fine.
        var sources = SourceFiles(Path.Combine(RepoRoot(), "src", "Api"))
            .Concat(SourceFiles(Path.Combine(RepoRoot(), "src", "Infrastructure")))
            .Select(f => (f, File.ReadAllText(f)));

        var offenders = Architecture.CrossTenantWriteGuard.FindOffenders(sources);
        Assert.True(offenders.Count == 0,
            $"QueryAllTenants() must not be composed with a set-based write:\n - {string.Join("\n - ", offenders)}");
    }

    [Fact]
    public void EveryTenantScopedEntity_HasAGlobalQueryFilter()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=arch-check") // model-only; never connects
            .Options;
        using var ctx = new AppDbContext(options, new TestCurrentTenant());

        var missing = ctx.Model.GetEntityTypes()
            .Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.True(missing.Count == 0,
            $"ITenantScoped entities missing the global tenant query filter: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryUserKeyedEntity_IsWiredIntoAccountErasure()
    {
        // Every entity with a user-owned key ("UserId") must be erased on account deletion (GDPR-2) —
        // by AccountErasureService's identity-core deletes, an IUserDataContributor, or tenant-membership
        // teardown. This canary fails when a NEW user-keyed entity appears, so its author must wire the
        // erasure and list it here (v2 audit SOLID-1 / R12). Actor references (CreatedByUserId /
        // InvitedByUserId) are deliberately excluded — they are tenant data, not the user's own PII.
        var handled = new HashSet<string>
        {
            nameof(UserLogin), nameof(RefreshToken),          // identity-core (AccountErasureService)
            nameof(TenantMembership),                          // tenant-membership teardown
            nameof(UserMfa), nameof(MfaRecoveryCode),          // MfaUserDataContributor
            nameof(Notification), nameof(NotificationPreference), // NotificationUserDataContributor
            nameof(EmailConnection),                            // EmailConnectionUserDataContributor (EMAIL-2, ADR-V002)
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=arch-check") // model-only; never connects
            .Options;
        using var ctx = new AppDbContext(options, new TestCurrentTenant());

        var uncovered = ctx.Model.GetEntityTypes()
            .Where(e => e.ClrType.GetProperty("UserId") is not null)
            .Select(e => e.ClrType.Name)
            .Where(name => !handled.Contains(name))
            .ToList();

        Assert.True(uncovered.Count == 0,
            $"User-keyed entities not wired into account erasure — add an IUserDataContributor (or identity-core delete) and list it: {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void EveryTenantOwnedEntity_IsWiredIntoTenantDissolution()
    {
        // The tenant-axis mirror of EveryUserKeyedEntity_IsWiredIntoAccountErasure (R82/R86, v3 LB-TEN-1):
        // every entity carrying a tenant-owned key ("TenantId") must be torn down when a tenant is dissolved
        // — by an ITenantDataContributor's WipeAsync, or the platform's core teardown
        // (ITenantRepository.WipeDataAsync). ITenantScoped entities have no FK to Tenants (ADR-003 plain-Guid
        // tenancy) so NOTHING cascades; this canary fails when a NEW tenant-owned entity appears, forcing its
        // author to wire the teardown (and, for GDPR-1, the export) and list it here — so a downstream slice
        // can't silently orphan a tenant's rows the way ApiKey/UsageCounter/WebhookSubscription/WebhookDelivery
        // did before LB-TEN-1 was fixed. Covers TenantId-carrying non-ITenantScoped entities too (WebhookDelivery).
        var handled = new HashSet<string>
        {
            nameof(BudgetSettings),                         // BudgetSettingsDataContributor (app slice BUDGET-1)
            nameof(Category),                               // CategoryDataContributor (app slice CATALOG-1)
            nameof(Bank),                                   // BankDataContributor (app slice CATALOG-2)
            nameof(Envelope),                               // EnvelopeDataContributor (app slice ENV-1)
            nameof(Month),                                  // LedgerDataContributor (app slice LEDGER-1/2)
            nameof(Week),                                   // LedgerDataContributor
            nameof(Transaction),                            // LedgerDataContributor
            nameof(Refund),                                 // LedgerDataContributor (app slice LEDGER-3)
            nameof(FixedExpense),                           // FixedExpenseDataContributor (app slice EXPENSES-1)
            nameof(VariableExpense),                        // VariableExpenseDataContributor (app slice EXPENSES-1)
            nameof(PendingVoucher), nameof(IngestedVoucher), // VoucherStagingDataContributor (app slice EMAIL-4)
            nameof(AuditEvent),                             // AuditDataContributor
            nameof(Subscription),                           // BillingDataContributor
            nameof(ApiKey),                                 // ApiKeyDataContributor
            nameof(UsageCounter),                           // UsageCounterDataContributor
            nameof(WebhookSubscription), nameof(WebhookDelivery), // WebhookDataContributor
            nameof(TenantInvitation), nameof(TenantMembership),   // core teardown (WipeDataAsync)
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=arch-check") // model-only; never connects
            .Options;
        using var ctx = new AppDbContext(options, new TestCurrentTenant());

        // Only a NON-NULLABLE Guid TenantId denotes single-tenant ownership. A nullable Guid? TenantId is
        // optional handler context on drained infrastructure — OutboxMessage carries one (and holds the
        // billing-cancel message the dissolve itself enqueues), so it must NOT be torn down. Excluding it by
        // the key's nullability is principled: an owned row always knows its tenant.
        var uncovered = ctx.Model.GetEntityTypes()
            .Where(e => e.ClrType.GetProperty("TenantId")?.PropertyType == typeof(Guid))
            .Select(e => e.ClrType.Name)
            .Where(name => !handled.Contains(name))
            .ToList();

        Assert.True(uncovered.Count == 0,
            "Tenant-owned entities not wired into tenant dissolution — add an ITenantDataContributor (or "
            + $"core teardown) and list it here (they will otherwise orphan on dissolve): {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void TenantDissolution_EntersTheTargetTenant()
    {
        // RLS-2/R83: DissolveAsync runs set-based deletes that the Postgres RLS backstop (ADR-020) scopes to
        // the AMBIENT tenant. To stay correct when a caller dissolves a tenant other than its ambient one
        // (account erasure of a solo tenant that isn't the JWT-current tenant, admin paths), the service must
        // enter the target so every delete is scoped to it — instead of relying on the CrossTenant-tag bypass
        // (which renders for ExecuteDelete but not ExecuteUpdate) and the Tenants FK cascade. Pin that it does.
        var file = Path.Combine(RepoRoot(), "src", "Api", "Services", "TenantDissolutionService.cs");
        var text = File.ReadAllText(file);
        Assert.Contains("EnterTenant(", text);
    }

    [Fact]
    public void WebApp_HasNoInlineBlazorComponents()
    {
        // The web app may only carry bootstrap markup; all UI components live in Shared.Ui (RCL).
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_Imports.razor", "App.razor", "Routes.razor",
        };

        var inline = SourceFiles(Path.Combine(RepoRoot(), "src", "Web"), "*.razor")
            .Select(Path.GetFileName)
            .Where(name => !allowed.Contains(name!))
            .ToList();

        Assert.True(inline.Count == 0,
            $"Blazor components belong in Shared.Ui, not the web app: {string.Join(", ", inline)}");
    }

    // ── v2 audit enforcement gates (B11) — lock in the B1–B7 fixes so they cannot silently regress ──

    [Fact]
    public void EveryEntityWithATenantId_IsScopedOrAllowlisted()
    {
        // R2/ARCH-2: an entity carrying a TenantId must be structurally filtered (ITenantScoped) or be on
        // an explicit allowlist of by-convention exceptions, so a new tenant-relevant table can't quietly
        // rely on hand-written filtering.
        var allow = new HashSet<string> { nameof(TenantMembership), nameof(WebhookDelivery) };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=arch-check") // model-only; never connects
            .Options;
        using var ctx = new AppDbContext(options, new TestCurrentTenant());

        var offenders = ctx.Model.GetEntityTypes() // mapped entities only — excludes transient DTOs
            .Where(e => e.ClrType.GetProperty("TenantId")?.PropertyType == typeof(Guid))
            .Where(e => !typeof(ITenantScoped).IsAssignableFrom(e.ClrType) && !allow.Contains(e.ClrType.Name))
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Entities with a TenantId must implement ITenantScoped or be allowlisted: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryController_DerivesFromATenantOrAdminBase_OrIsAllowlisted()
    {
        // R4: a copied controller must not silently skip tenant/staff auth — it derives from a base that
        // applies it, or it is an explicitly-allowlisted system/anonymous surface.
        var allow = new HashSet<string>
        {
            // The /api/auth surface is split across focused controllers (B9-1/SOLID-2) that all derive from
            // AuthControllerBase — an anonymous/JWT auth surface, not a tenant-scoped one.
            nameof(AuthController), nameof(AccountController), nameof(MfaController), nameof(NativeAuthController),
            "FilesController", nameof(BillingWebhookController), nameof(NotificationsController),
        };

        var offenders = typeof(TenantApiControllerBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => !typeof(TenantApiControllerBase).IsAssignableFrom(t)
                     && !typeof(AdminApiControllerBase).IsAssignableFrom(t)
                     && !allow.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Controllers must derive from TenantApiControllerBase/AdminApiControllerBase or be allowlisted: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ServerServices_UseInjectedClock_NotAmbientUtcNow()
    {
        // R15/GAP-4/LOGIC-B3: server code takes TimeProvider, so a cookie/URL/token lifetime can't drift
        // from the injected clock. Entity default-value helpers and the WASM client (no injected clock)
        // live in Core/Shared.Ui and are outside this scan; migrations are excluded.
        var dirs = new[] { Path.Combine(RepoRoot(), "src", "Api"), Path.Combine(RepoRoot(), "src", "Infrastructure") };

        var offenders = dirs.SelectMany(d => SourceFiles(d))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f) is var t && (t.Contains("DateTime.UtcNow") || t.Contains("DateTimeOffset.UtcNow")))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Server services must use an injected TimeProvider, not ambient UtcNow: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void PlatformTests_DoNotDependOnTheDeleteMeNotesSample()
    {
        // R9/TR-1: the tenancy/GDPR/outbox tests use the harness TestWidget fixture, not the DELETE-ME
        // Notes sample — so a downstream app can delete the sample without breaking the isolation guards.
        var testsDir = Path.Combine(RepoRoot(), "tests");
        string[] banned = ["Features.Notes", "Set<Note>", "new Note", "EfRepository<Note>", "IRepository<Note>"];

        // NotesSliceTests legitimately tests the sample; this file lists the banned patterns as literals.
        var exempt = new HashSet<string> { "NotesSliceTests.cs", "ArchitectureTests.cs" };
        var offenders = SourceFiles(testsDir)
            .Where(f => !exempt.Contains(Path.GetFileName(f)!))
            .Where(f => File.ReadAllText(f) is var t && Array.Exists(banned, t.Contains))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Platform tests must use the harness TestWidget fixture, not the DELETE-ME Notes sample: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TenantDataContributors_HaveUniqueExportKeys()
    {
        // R13/TR-9: the export bundle is keyed by ITenantDataContributor.ExportKey, and dissolve fans out
        // over the same set — two contributors sharing a key would silently overwrite each other's export
        // slice (and confuse dissolve). Reflect every concrete contributor across the platform assemblies
        // and assert the keys are distinct + non-blank. Uninitialized instances are enough: ExportKey
        // getters return constants, so no constructor/DI is needed.
        var assemblies = new[] { typeof(TenantApiControllerBase).Assembly, typeof(AppDbContext).Assembly };
        var keys = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantDataContributor).IsAssignableFrom(t))
            .Select(t => ((ITenantDataContributor)RuntimeHelpers.GetUninitializedObject(t)).ExportKey)
            .ToList();

        Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k), "ExportKey must be non-blank."));
        var dupes = keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"ITenantDataContributor.ExportKey values must be unique: {string.Join(", ", dupes)}");
    }

    [Fact]
    public void TenantScopedEntities_MapToDistinctTables()
    {
        // R35 (table half)/ADV-4: two parallel slices must not silently map to the same table. Assert every
        // mapped entity resolves to a distinct (schema, table) pair.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=arch-check") // model-only; never connects
            .Options;
        using var ctx = new AppDbContext(options, new TestCurrentTenant());

        var dupes = ctx.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .GroupBy(e => $"{e.GetSchema()}.{e.GetTableName()}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} <- {string.Join(" & ", g.Select(e => e.ClrType.Name))}")
            .ToList();

        Assert.True(dupes.Count == 0, $"Entities must map to distinct tables: {string.Join("; ", dupes)}");
    }

    [Fact]
    public void RouteGroupPrefixes_AreUnique()
    {
        // R35 (route half)/R36/ADV-P4-1: no two slices claim the same /api/<x> prefix. Collect controller
        // [Route] templates and minimal-API group literals; assert the full route strings are distinct.
        // The collector matches BOTH raw MapGroup("…") (the platform Endpoints/ surfaces) and the mandated
        // MapTenantFeatureGroup("…") (feature slices) — the old gate matched only the raw form, so feature
        // prefixes were invisible and two slices could share a prefix green (v3 ADV-P4-1). It now scans
        // Endpoints/ as well as Features/, closing Step-0 gap S0-G1.
        var controllersDir = Path.Combine(RepoRoot(), "src", "Api", "Controllers");
        var featuresDir = Path.Combine(RepoRoot(), "src", "Api", "Features");
        var endpointsDir = Path.Combine(RepoRoot(), "src", "Api", "Endpoints");

        var controllerSources = SourceFiles(controllersDir).Select(File.ReadAllText);
        var groupSources = SourceFiles(endpointsDir).Concat(SourceFiles(featuresDir)).Select(File.ReadAllText);

        var dupes = Architecture.RoutePrefixInspector.FindDuplicatePrefixes(controllerSources, groupSources);
        Assert.True(dupes.Count == 0,
            $"Route prefixes must be unique across controllers, feature groups, and endpoint groups: {string.Join(", ", dupes)}");
    }

    [Fact]
    public void FeatureFolders_DoNotReferenceEachOthersNamespaces()
    {
        // R7/TR-9: a vertical slice under Features/<X>/ must stay self-contained — it may not reference
        // another slice's Features.<Y> namespace. Keeps slices independently deletable.
        var featuresDir = Path.Combine(RepoRoot(), "src", "Api", "Features");
        var slices = Directory.Exists(featuresDir)
            ? Directory.GetDirectories(featuresDir).Select(Path.GetFileName).ToList()
            : [];

        var offenders = new List<string>();
        foreach (var slice in slices)
        {
            var others = slices.Where(s => !string.Equals(s, slice, StringComparison.Ordinal));
            foreach (var file in SourceFiles(Path.Combine(featuresDir, slice!)))
            {
                var text = File.ReadAllText(file);
                if (others.Any(o => text.Contains($"Vuelto.Api.Features.{o}", StringComparison.Ordinal)))
                    offenders.Add(Path.GetFileName(file)!);
            }
        }

        Assert.True(offenders.Count == 0,
            $"Feature slices must not reference another slice's namespace: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyProgram_ReferencesFeatureNamespaces_FromOutsideFeatures()
    {
        // R8: the clean state is that composition happens in one place — only Program.cs wires the feature
        // endpoints. Nothing else outside src/Api/Features/ may reach into Vuelto.Api.Features.*.
        var apiDir = Path.Combine(RepoRoot(), "src", "Api");
        var featuresPath = $"{Path.DirectorySeparatorChar}Features{Path.DirectorySeparatorChar}";

        var offenders = SourceFiles(apiDir)
            .Where(f => !f.Contains(featuresPath))                     // scope: outside the Features tree
            .Where(f => Path.GetFileName(f) != "Program.cs")           // Program.cs is the sanctioned composer
            .Where(f => File.ReadAllText(f).Contains("Vuelto.Api.Features", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Only Program.cs may reference Vuelto.Api.Features.* from outside Features/: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void FeatureFiles_RegisterRoutesViaMapTenantFeatureGroup_NotRawMapGroup()
    {
        // R6/D7: a vertical slice under Features/ registers its routes through the shared
        // MapTenantFeatureGroup helper (which applies the tenant-API auth policy), never a raw
        // app.MapGroup(...). Config-gated PLATFORM surfaces (PUBAPI/HOOKS) that legitimately need a
        // raw MapGroup live under src/Api/Endpoints/, not Features/ (amended ADR-004) — so this scan
        // stays clean. It fails the moment a future slice reaches for a raw MapGroup.
        var featuresDir = Path.Combine(RepoRoot(), "src", "Api", "Features");

        var offenders = SourceFiles(featuresDir)
            .Where(f => File.ReadAllText(f).Contains(".MapGroup(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Feature slices must register routes via MapTenantFeatureGroup, not a raw MapGroup: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void SourceFile_DeclaresATypeMatchingItsName()
    {
        // R24 (naming half, ~MA0048): a source file declares a type that matches its file name, so a
        // reader can find `Foo` in `Foo.cs`. Intentional DTO/settings aggregation files (which hold many
        // small records named for the feature, not the file) are the sanctioned exception: any
        // `*Models.cs`, plus the two named aggregations. Enforced as a source scan rather than the
        // Meziantou MA0048 analyzer, which would enable ~150 unrelated rules under warnings-as-error.
        var allow = new HashSet<string>(StringComparer.Ordinal) { "SettingsProvider", "WebhookService" };
        var typeDecl = new Regex(
            @"\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|ref\s+)*(?:class|record|interface|enum|struct)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in SourceFiles(Path.Combine(RepoRoot(), "src")))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileNameWithoutExtension(file);           // Foo.cs → Foo
            if (name.EndsWith(".xaml", StringComparison.Ordinal)) name = name[..^5]; // App.xaml.cs → App
            if (name.EndsWith("Models", StringComparison.Ordinal) || allow.Contains(name)) continue;

            var types = typeDecl.Matches(File.ReadAllText(file))
                .Select(m => Regex.Replace(m.Groups[1].Value, "<.*", "")).ToHashSet(StringComparer.Ordinal);
            if (types.Count > 0 && !types.Contains(name))               // files with no top-level type are fine
                offenders.Add(Path.GetFileName(file)!);
        }

        Assert.True(offenders.Count == 0,
            $"Each source file must declare a type matching its name (or be a *Models aggregation): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void IntegrationFactory_DoesNotBackfillRlsPolicies()
    {
        // v3 audit RLS-1: the migration-parity gate (RlsMigrationGateTests) is only honest if the database
        // it inspects gets its RLS policies from MIGRATIONS, not back-filled from the model. So the
        // migration-based harness must provision the runtime ROLE only — never RlsDdl.StatementsFor, and
        // never the policy-applying RlsTestSetup.ProvisionAsync. If it did, a slice could ship an
        // ITenantScoped table with no RLS migration and still pass CI, reaching production unprotected.
        var factory = Path.Combine(RepoRoot(), "tests", "Api.Tests", "Infrastructure", "IntegrationTestFactory.cs");
        var text = File.ReadAllText(factory);

        Assert.DoesNotContain("RlsDdl.StatementsFor", text);
        Assert.DoesNotContain("RlsTestSetup.ProvisionAsync", text);
    }

    [Fact]
    public void OutboundHttpSenders_RouteThroughTheUrlGuard_OrAreAllowlisted()
    {
        // R76 machine half (v3 audit S0-G4, T48): server-initiated HTTP to a DYNAMIC URL is the SSRF
        // surface (v2 GAP-2) — every server-side file that actually SENDS via HttpClient must either
        // reference IOutboundUrlGuard (WebhookSender is the model) or be allowlisted here with a
        // rationale saying why its destinations are not attacker-influenced. Registration-only files
        // (AddHttpClient with no send call) pass automatically. File-level granularity, matching the
        // other source scans in this class.
        var allowlisted = new Dictionary<string, string>
        {
            // WebhookSender, the only dynamic-URL sender, injects the guard. App allowlist (FX-1, ADR-V006):
            ["ExchangeRateApiClient.cs"] = "destination = the configured vendor host (ExchangeRate:BaseUrl) + API key + two currency codes validated as ^[A-Z]{3}$ — nothing tenant-supplied reaches the URL",
            // EMAIL-2/3 (ADR-V016): fixed provider hosts only — nothing user-supplied reaches a URL.
            ["MailConsentService.cs"] = "destination = the two fixed IdP token endpoints (login.microsoftonline.com/{configured tenant}, oauth2.googleapis.com); the code/refresh token travel in the form body",
            ["GraphEmailReader.cs"] = "destination = graph.microsoft.com only — folder ids are URL-escaped path segments and the @odata.nextLink is followed only when its host is graph.microsoft.com",
            ["GmailEmailReader.cs"] = "destination = gmail.googleapis.com only — message ids are URL-escaped path segments, the search string is a query value",
        };

        var send = new Regex(
            @"\.(SendAsync|PostAsync|PostAsJsonAsync|GetAsync|GetStringAsync|GetFromJsonAsync|GetByteArrayAsync|PutAsync|PutAsJsonAsync|PatchAsync|DeleteAsync)\s*\(");
        var offenders = new List<string>();
        foreach (var dir in new[] { "Api", "Infrastructure", "Core" }) // server-side; the client talks only to its own API
        foreach (var f in SourceFiles(Path.Combine(RepoRoot(), "src", dir)))
        {
            var text = File.ReadAllText(f);
            if (!text.Contains("HttpClient") || !send.IsMatch(text))
                continue; // not an HTTP sender
            if (text.Contains("IOutboundUrlGuard") || allowlisted.ContainsKey(Path.GetFileName(f)))
                continue;
            offenders.Add(Path.GetFileName(f));
        }

        Assert.True(offenders.Count == 0,
            "Server-side HTTP senders that neither use IOutboundUrlGuard nor carry an allowlist rationale "
            + $"(SSRF surface — route tenant-supplied URLs through the guard): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OtpNet_IsConfinedToMfaService()
    {
        // v3 audit TOOL-4 (T50, R66): Otp.NET is effectively single-maintainer AND on the MFA path —
        // accepted (tiny RFC-6238 surface, no I/O), but only while it stays a ONE-FILE dependency:
        // confined to MfaService so it can be swapped for another implementation (or inline HMAC)
        // behind IMfaService without touching anything else. Tests may reference it freely (they
        // generate codes to drive the real service). See docs/TECH_STACK.md.
        var offenders = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .Where(f => File.ReadAllText(f).Contains("OtpNet"))
            .Select(f => Path.GetFileName(f))
            .Where(name => name != "MfaService.cs")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Otp.NET must stay confined to MfaService (swappable-seam contract, TOOL-4): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void FeatureErrors_UseTheSharedErrorResponse_NotAdHocShapes()
    {
        // R76 (v3 audit TR-5, T53): slice error bodies must be the shared ErrorResponse record —
        // clients key their copy off `error` codes with a stable contract, and Phase 4 proved the
        // ad-hoc `new { error … }` shape propagates: copying the Notes exemplar reproduced it
        // verbatim. Banned across the whole HTTP surface — Features/ + Endpoints/ AND Controllers/
        // (the post-campaign reconcile found a straggler in BillingWebhookController the narrower
        // scope missed); the exemplar models the right shape.
        var anonError = new Regex(@"new\s*\{\s*error\b");
        var offenders = SourceFiles(Path.Combine(RepoRoot(), "src", "Api", "Features"))
            .Concat(SourceFiles(Path.Combine(RepoRoot(), "src", "Api", "Endpoints")))
            .Concat(SourceFiles(Path.Combine(RepoRoot(), "src", "Api", "Controllers")))
            .Where(f => anonError.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Ad-hoc anonymous error shapes in the slice surface — use the shared ErrorResponse record: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<string> SourceFiles(string dir, string pattern = "*.cs") =>
        !Directory.Exists(dir)
            ? []
            : Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api", "Features")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
