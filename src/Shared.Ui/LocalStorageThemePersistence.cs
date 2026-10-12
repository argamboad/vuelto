using Microsoft.JSInterop;

namespace Vuelto.Shared.Ui;

/// <summary>Both hosts: the same <c>localStorage["app_theme"]</c> key the pre-paint bootstrap
/// (<c>wwwroot/js/theme.js</c>) reads at page load.</summary>
public class LocalStorageThemePersistence(IJSRuntime js) : IThemePersistence
{
    public const string StorageKey = "app_theme";

    public async Task PersistAsync(string theme)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, theme); }
        catch { /* best-effort — the in-process theme set by the caller still applies */ }
    }

    public async Task ClearAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch { /* best-effort */ }
    }

    public async Task<string?> GetAsync()
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", StorageKey); }
        catch { return null; /* storage unavailable — treat as "never chose" */ }
    }
}
