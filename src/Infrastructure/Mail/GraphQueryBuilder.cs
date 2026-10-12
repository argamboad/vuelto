using System.Globalization;
using Vuelto.Core.Mail;

namespace Vuelto.Infrastructure.Mail;

/// <summary>
/// Builds the Microsoft Graph message query for an <see cref="EmailQuery"/> — every filter is pushed
/// into <c>$filter</c> so only matching mail is retrieved: <c>received ≥ cursor</c> AND (when set)
/// <c>isRead eq false</c> AND (any sender OR any subject). Senders match as exact addresses, subjects as
/// <c>startswith</c>. No <c>$orderby</c>: Graph rejects it next to string functions / cross-property OR
/// ("restriction or sort order is too complex"), so the reader sorts. Pure — no I/O.
/// </summary>
public static class GraphQueryBuilder
{
    private const string Select = "id,subject,from,receivedDateTime,body";

    public static string BuildFilter(EmailQuery q)
    {
        var clauses = new List<string> { $"receivedDateTime ge {q.ReceivedAfter.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}" };
        if (q.UnreadOnly) clauses.Add("isRead eq false");

        var voucher = new List<string>();
        foreach (var s in q.Senders) voucher.Add($"from/emailAddress/address eq '{Escape(s)}'");
        foreach (var sub in q.Subjects) voucher.Add($"startswith(subject,'{Escape(sub)}')");
        if (voucher.Count > 0) clauses.Add("(" + string.Join(" or ", voucher) + ")");

        return string.Join(" and ", clauses);
    }

    /// <summary>Relative request URL for one folder (or the whole mailbox when null/empty).</summary>
    public static string MessagesUrl(string? folderId, EmailQuery q)
    {
        var basePath = string.IsNullOrWhiteSpace(folderId) ? "/me/messages" : $"/me/mailFolders/{Uri.EscapeDataString(folderId)}/messages";
        return $"{basePath}?$filter={Uri.EscapeDataString(BuildFilter(q))}&$select={Uri.EscapeDataString(Select)}&$top={q.MaxResults.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
