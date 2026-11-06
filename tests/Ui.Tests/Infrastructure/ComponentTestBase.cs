using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Vuelto.Shared.Ui;
using Vuelto.Shared.Ui.Resources;
using Perezosoft.Testing.Ui;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Ui.Tests.Infrastructure;

/// <summary>
/// Base for this repo's component tests: the packaged harness (<see cref="PlatformComponentTestBase"/>, Pkg 5.1c) plus
/// the things it cannot know — the seeded UI's own strings, served by the deterministic localizer, and vuelto's
/// own client services.
/// </summary>
public abstract class ComponentTestBase : PlatformComponentTestBase
{
    protected ComponentTestBase()
    {
        Services.AddSingleton<IStringLocalizer<AppStrings>>(new FakeStringLocalizer<AppStrings>());
        // vuelto's own client services, as both hosts register them.
        Services.AddSingleton<ReviewQueueNotifier>(); // the header badge + the Review page (EMAIL-6)
        Services.AddScoped<DisplayCurrencyStore>();   // the dashboard + Reports "show in" (DISPLAY-1)
    }
}
