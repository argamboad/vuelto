using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using Vuelto.Shared.Ui.Auth;
using Vuelto.Shared.Ui.Components;

namespace Vuelto.Shared.Ui;

/// <summary>
/// The "show amounts in" preference (₡ · $ · both) the dashboard and the Reports tables share — kept the way the
/// platform keeps the theme (ADR-V020): a device copy (<c>appUi</c> prefs) so the page can paint before any
/// call, and the account copy (<c>/api/display-settings</c>) so the choice follows the person to the phone and
/// any other browser. On load the account wins once the user has chosen there; until then the device's choice
/// is adopted and pushed up, so a pre-existing local choice is never lost. Saves go to both; the API save is
/// best-effort (the local choice still applies for now).
/// </summary>
public sealed class DisplayCurrencyStore(HttpClient http, IJSRuntime js, AuthService auth)
{
    public async Task<string> LoadAsync()
    {
        var device = MoneyDisplay.Both;
        try { device = MoneyDisplay.Parse(await js.InvokeAsync<string?>("appUi.getPref", MoneyDisplay.PrefKey)); }
        catch { /* no storage — both sides */ }
        if (!auth.IsAuthenticated) return device;

        try
        {
            var res = await http.GetAsync("/api/display-settings");
            if (!res.IsSuccessStatusCode) return device;
            var account = await res.Content.ReadFromJsonAsync<AccountDto>();
            if (account is null) return device;
            if (account.IsDefault)
            {
                if (device != MoneyDisplay.Both) await PutAsync(device); // adopt the device's choice, like the theme on first sign-in
                return device;
            }
            var chosen = MoneyDisplay.Parse(account.DisplayCurrency);
            if (chosen != device) await PersistDeviceAsync(chosen);
            return chosen;
        }
        catch { return device; }
    }

    public async Task SaveAsync(string display)
    {
        await PersistDeviceAsync(display);
        if (auth.IsAuthenticated) await PutAsync(display);
    }

    private async Task PersistDeviceAsync(string display)
    {
        try { await js.InvokeVoidAsync("appUi.setPref", MoneyDisplay.PrefKey, display); } catch { /* preference only */ }
    }

    private async Task PutAsync(string display)
    {
        try { await http.PutAsJsonAsync("/api/display-settings", new { display_currency = display }); } catch { /* best-effort */ }
    }

    private sealed record AccountDto(
        [property: JsonPropertyName("display_currency")] string? DisplayCurrency,
        [property: JsonPropertyName("is_default")] bool IsDefault);
}
