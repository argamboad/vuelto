using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Minimal client for the dev Mailpit REST API (http://localhost:8025) so E2E tests can read
/// the OTP code / magic link the app "sends" — the same trick a manual tester uses.
/// </summary>
public static class Mailpit
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("MAILPIT_BASE_URL") ?? "http://localhost:8025"),
    };

    /// <summary>Deletes all trapped messages (call before triggering a fresh email).</summary>
    public static Task ClearAsync() => Http.DeleteAsync("/api/v1/messages");

    /// <summary>
    /// Polls until an OTP email addressed to <paramref name="toEmail"/> arrives and returns its
    /// 6-digit code. Throws on timeout.
    /// </summary>
    public static async Task<string> WaitForOtpAsync(string toEmail, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var code = await TryGetLatestCodeAsync(toEmail);
            if (code is not null) return code;
            await Task.Delay(500);
        }
        throw new TimeoutException($"No OTP email for {toEmail} within {timeout.TotalSeconds:0}s.");
    }

    /// <summary>
    /// Polls until a magic-link email addressed to <paramref name="toEmail"/> arrives and returns
    /// the sign-in URL it contains. Throws on timeout.
    /// </summary>
    public static async Task<string> WaitForMagicLinkAsync(string toEmail, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var link = await TryGetLatestMagicLinkAsync(toEmail);
            if (link is not null) return link;
            await Task.Delay(500);
        }
        throw new TimeoutException($"No magic-link email for {toEmail} within {timeout.TotalSeconds:0}s.");
    }

    private static async Task<string?> TryGetLatestMagicLinkAsync(string toEmail)
    {
        var list = await Http.GetFromJsonAsync<MessageList>("/api/v1/messages?limit=50");
        var summary = list?.Messages?
            .FirstOrDefault(m =>
                m.To.Any(a => string.Equals(a.Address, toEmail, StringComparison.OrdinalIgnoreCase))
                && (m.Subject?.Contains("sign-in link", StringComparison.OrdinalIgnoreCase) ?? false));
        if (summary is null) return null;

        var detail = await Http.GetFromJsonAsync<MessageDetail>($"/api/v1/message/{summary.ID}");
        var match = Regex.Match(detail?.HTML ?? "", @"https?://[^""'\s]*magic-link/verify[^""'\s]*");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Value) : null;
    }

    // OTP emails are localized to the requester's UI language (QA-I18N-04), so accept every
    // shipped translation of the subject ("Your verification code").
    private static readonly string[] OtpSubjects = ["verification code", "código de verificación"];

    private static async Task<string?> TryGetLatestCodeAsync(string toEmail)
    {
        var list = await Http.GetFromJsonAsync<MessageList>("/api/v1/messages?limit=50");
        // Match on the OTP subject too — other emails to the same address (e.g. an invitation,
        // delivered late by the outbox) could otherwise satisfy the 6-digit regex.
        var summary = list?.Messages?
            .FirstOrDefault(m =>
                m.To.Any(a => string.Equals(a.Address, toEmail, StringComparison.OrdinalIgnoreCase))
                && OtpSubjects.Any(s => m.Subject?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        if (summary is null) return null;

        var detail = await Http.GetFromJsonAsync<MessageDetail>($"/api/v1/message/{summary.ID}");
        var body = $"{detail?.Text} {detail?.HTML}";
        var match = Regex.Match(body, @"(?<!\d)(\d{6})(?!\d)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private sealed record MessageList(List<MessageSummary>? Messages);
    private sealed record MessageSummary(string ID, List<EmailAddress> To, string? Subject);
    private sealed record EmailAddress(string Address);
    private sealed record MessageDetail(string? Text, string? HTML);
}
