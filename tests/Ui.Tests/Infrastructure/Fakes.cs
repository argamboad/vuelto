using Microsoft.Extensions.Localization;
using Vuelto.Shared.Ui;
using Vuelto.Shared.Ui.Resources;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Ui.Tests.Infrastructure;

/// <summary>In-memory <see cref="ISessionStore"/>. Web-parity by default (cookie transport, no body token).</summary>
public sealed class FakeSessionStore(bool usesBodyTransport = false) : ISessionStore
{
    private string? _token;
    public bool UsesBodyTransport { get; } = usesBodyTransport;
    public Task<string?> GetRefreshTokenAsync() => Task.FromResult(_token);
    public Task SaveRefreshTokenAsync(string refreshToken) { _token = refreshToken; return Task.CompletedTask; }
    public Task ClearAsync() { _token = null; return Task.CompletedTask; }
}

/// <summary>In-memory <see cref="IThemePersistence"/> — starts unset (user never chose).</summary>
public sealed class FakeThemePersistence : IThemePersistence
{
    public string? Value { get; private set; }
    public Task PersistAsync(string theme) { Value = theme; return Task.CompletedTask; }
    public Task<string?> GetAsync() => Task.FromResult(Value);
    public Task ClearAsync() { Value = null; Cleared = true; return Task.CompletedTask; }
    public bool Cleared { get; private set; }
}

/// <summary>
/// In-memory <see cref="ICulturePersistence"/> — starts unset. Set <see cref="WritesBlocked"/> to model a
/// store that's readable but NOT writable (quota/policy): writes are silently swallowed, exactly the
/// condition that could loop the locale reload forever (UX-2).
/// </summary>
public sealed class FakeCulturePersistence : ICulturePersistence
{
    public string? Value { get; private set; }
    public bool WritesBlocked { get; set; }
    public Task PersistAsync(string cultureCode) { if (!WritesBlocked) Value = cultureCode; return Task.CompletedTask; }
    public Task<string?> GetAsync() => Task.FromResult(Value);
    public Task ClearAsync() { Value = null; Cleared = true; return Task.CompletedTask; }
    public bool Cleared { get; private set; }
}

/// <summary>
/// Deterministic <see cref="IStringLocalizer{AppStrings}"/>: returns the key itself as the text (with a
/// "[arg, …]" suffix when formatted), so component assertions pin a stable key + its arguments rather than
/// coupling to a shipped translation. Never "not found" — a missing key just renders as the key.
/// </summary>
public sealed class FakeStringLocalizer : IStringLocalizer<AppStrings>
{
    public LocalizedString this[string name] => new(name, name, resourceNotFound: false);

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var value = arguments.Length == 0 ? name : $"{name}[{string.Join(", ", arguments)}]";
            return new LocalizedString(name, value, resourceNotFound: false);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
