using Microsoft.Playwright;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// GDPR export download journey (GDPR-1 UI + NATIVE-3). The owner requests an export and the
/// Download control produces a real browser download — the signed URL is served with
/// Content-Disposition: attachment, so the page is never replaced by raw JSON and the same
/// launcher seam works inside a native WebView. Maps to the QA plan's export case.
/// </summary>
[TestFixture]
public class GdprExportJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Owner_Downloads_The_Data_Export()
    {
        var household = await SignInToHouseholdAsync(Page, UniqueEmail("exporter"));

        await Expect(household.ExportData).ToBeVisibleAsync(Slow);
        await household.ExportData.ClickAsync();
        await Expect(household.ExportDownload).ToBeVisibleAsync(Slow);

        var download = await Page.RunAndWaitForDownloadAsync(
            () => household.ExportDownload.ClickAsync(),
            new() { Timeout = 30_000 });

        Assert.That(download.SuggestedFilename, Does.EndWith(".json"),
            "the export should download as the JSON bundle named by the server");

        // The download must not have navigated the app away from the Household page.
        await Expect(household.ExportDownload).ToBeVisibleAsync();
    }
}
