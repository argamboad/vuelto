using Microsoft.JSInterop;

namespace Perezosoft.Shared.Ui;

/// <summary>Web implementation: the same <c>localStorage["app_culture"]</c> key the WASM
/// bootstrap (<c>Web/Program.cs</c>) reads before first render.</summary>
public class LocalStorageCulturePersistence(IJSRuntime js) : ICulturePersistence
{
    public const string StorageKey = "app_culture";

    public async Task PersistAsync(string cultureCode)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, cultureCode); }
        catch { /* best-effort — the in-process culture set by the caller still applies */ }
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
