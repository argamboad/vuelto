using Microsoft.Playwright;

namespace Perezosoft.E2E.Tests.Pages;

// Inherit from this for every page object. Path is relative to the app base URL.
// Base URL is set via PLAYWRIGHT_BASE_URL env var or playwright.runsettings.
public abstract class BasePage(IPage page)
{
    protected IPage Page { get; } = page;

    public abstract string Path { get; }

    public Task GotoAsync() => Page.GotoAsync(Path);
}
