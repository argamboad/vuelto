using Microsoft.JSInterop;

namespace Vuelto.Shared.Ui;

/// <summary>
/// Browser <c>confirm()</c> helper that FAILS CLOSED. If the JS-interop call throws — prerender,
/// a disconnected circuit, no DOM — this returns <c>false</c> so a destructive action (remove
/// member, leave, dissolve, unlink) is cancelled rather than silently proceeding without the user's
/// confirmation (CONF-15). Every destructive flow goes through this one helper.
/// </summary>
public static class JsConfirm
{
    public static async Task<bool> ConfirmAsync(this IJSRuntime js, string message)
    {
        try { return await js.InvokeAsync<bool>("confirm", message); }
        catch { return false; }
    }
}
