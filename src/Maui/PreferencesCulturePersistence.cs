using Vuelto.Shared.Ui;

namespace Vuelto.Maui;

/// <summary>
/// Native implementation: OS <see cref="Preferences"/>, because that's the only store
/// <c>MauiProgram</c> can read at startup — the WebView (and its localStorage) doesn't exist
/// yet when the process boots. Key mirrors the web's localStorage key for greppability.
/// </summary>
public class PreferencesCulturePersistence : ICulturePersistence
{
    public const string PreferenceKey = "app_culture";

    public Task PersistAsync(string cultureCode)
    {
        Preferences.Default.Set(PreferenceKey, cultureCode);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync() =>
        Task.FromResult(Preferences.Default.Get<string?>(PreferenceKey, null));

    public Task ClearAsync()
    {
        Preferences.Default.Remove(PreferenceKey);
        return Task.CompletedTask;
    }
}
