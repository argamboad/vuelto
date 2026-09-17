using Microsoft.Playwright;

namespace Vuelto.E2E.Tests;

/// <summary>
/// REPORTS-7/8 journey. A member loads a date range on Reports (a range needs no month, so a fresh household can
/// do it), opens the PDF dialog and downloads: the API renders the report, stores it behind the signed link, and the
/// shared launcher turns it into a real browser download named by the server — the page stays where it was. Then
/// "Email me" queues the same file to the member's own address, and Mailpit receives it attached. Maps to the QA
/// plan's report PDF and "Email me" cases.
/// </summary>
[TestFixture]
public class ReportPdfJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Member_Downloads_The_Report_As_A_Pdf_And_Emails_It_To_Themselves()
    {
        var email = UniqueEmail("reports");
        await Mailpit.ClearAsync();
        await SignInAsync(Page, email);
        await Page.GotoAsync("/reports");

        await Page.GetByTestId("rep-mode").SelectOptionAsync("range");
        await Page.GetByTestId("rep-from").FillAsync("2026-06-01");
        await Page.GetByTestId("rep-to").FillAsync("2026-06-30");
        await Page.GetByTestId("rep-load").ClickAsync();
        await Expect(Page.GetByTestId("rep-period")).ToBeVisibleAsync(Slow);

        await Page.GetByTestId("rep-pdf").ClickAsync();
        await Expect(Page.GetByTestId("rep-pdf-dialog")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("rep-pdf-col-notes")).ToBeCheckedAsync(); // every column starts ticked (REPORTS-9)
        await Page.GetByTestId("rep-pdf-col-notes").UncheckAsync();            // a leaner appendix still renders

        var download = await Page.RunAndWaitForDownloadAsync(
            () => Page.GetByTestId("rep-pdf-download").ClickAsync(),
            new() { Timeout = 60_000 });

        Assert.That(download.SuggestedFilename, Is.EqualTo("report-2026-06-01_2026-06-30.pdf"),
            "the PDF should download under the name the server chose");
        await Expect(Page.GetByTestId("rep-pdf-dialog")).Not.ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("rep-notice")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("reports-page")).ToBeVisibleAsync(); // never navigated away

        await Page.GetByTestId("rep-pdf").ClickAsync();
        await Page.GetByTestId("rep-pdf-email").ClickAsync();
        await Expect(Page.GetByTestId("rep-pdf-dialog")).Not.ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("rep-notice")).ToContainTextAsync(email, new() { Timeout = 30_000 });

        var (_, fileName, contentType) = await Mailpit.WaitForAttachmentAsync(email, TimeSpan.FromSeconds(60));
        Assert.That(fileName, Is.EqualTo("report-2026-06-01_2026-06-30.pdf"), "the email should carry the same file");
        Assert.That(contentType, Is.EqualTo("application/pdf"));
    }
}
