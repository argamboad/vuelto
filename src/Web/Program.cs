using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Perezosoft.Client.Web;
using Vuelto.Shared.Ui;
using Perezosoft.Client;

// The web host (Pkg 4.2, ADR-034): the app's root components, then the platform's client for the browser
// (Perezosoft.Platform.Web: API clients, session, preferences), the saved culture, run.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.AddPlatformWeb();

// vuelto's own client services (Shared.Ui): the review-queue badge and the ₡ · $ · both preference.
builder.Services.AddSingleton<ReviewQueueNotifier>(); // EMAIL-6: the header badge re-counts when the queue changes
builder.Services.AddScoped<DisplayCurrencyStore>();   // DISPLAY-1: device copy + account copy
var host = builder.Build();
await host.UseStoredCultureAsync();
await host.RunAsync();
