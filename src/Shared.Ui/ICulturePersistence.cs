namespace Vuelto.Shared.Ui;

/// <summary>
/// Persists the user's chosen UI culture so the host can re-apply it on the next cold start.
/// Each host owns a store its startup path can actually read: web writes
/// <c>localStorage["app_culture"]</c> (read pre-render by <c>Web/Program.cs</c>); MAUI writes
/// OS <c>Preferences</c> (read in <c>MauiProgram</c> — WebView localStorage doesn't exist yet
/// when the native process boots). Callers still set the in-process culture themselves; this
/// is only the durable half. <see cref="GetAsync"/> reads the store back so the sign-in
/// reconcile can adopt a device-local choice server-side (PREFS-1, ADR-022).
/// </summary>
public interface ICulturePersistence
{
    Task PersistAsync(string cultureCode);

    /// <summary>The device-stored culture code, or null when the user never chose one.</summary>
    Task<string?> GetAsync();
    /// <summary>Removes the device-stored culture (sign-out, so the next user can't inherit it — ADM-9).</summary>
    Task ClearAsync();
}
