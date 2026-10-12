using System.Globalization;
using Vuelto.Core.Mail;

namespace Vuelto.Infrastructure.Mail;

/// <summary>
/// Builds the Gmail <c>q</c> search for an <see cref="EmailQuery"/> — every filter pushed into the query:
/// folder labels AND (any sender OR any subject) AND <c>after:</c> the cursor AND (when set)
/// <c>is:unread</c>. Gmail ANDs terms by default and uses <c>{ }</c> for OR groups. Pure — no I/O.
/// </summary>
public static class GmailQueryBuilder
{
    public static string BuildQ(EmailQuery q)
    {
        var terms = new List<string> { q.Folders.Count > 0 ? OrGroup(q.Folders.Select(f => $"label:{Label(f)}")) : "in:inbox" };

        var voucher = new List<string>();
        if (q.Senders.Count > 0) voucher.Add("from:(" + string.Join(" OR ", q.Senders) + ")");
        if (q.Subjects.Count > 0) voucher.Add("subject:(" + string.Join(" OR ", q.Subjects.Select(Quote)) + ")");
        if (voucher.Count > 0) terms.Add(voucher.Count == 1 ? voucher[0] : "{" + string.Join(" ", voucher) + "}");

        terms.Add($"after:{q.ReceivedAfter.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}"); // exclusive; the overlap covers it
        if (q.UnreadOnly) terms.Add("is:unread");
        return string.Join(" ", terms);
    }

    private static string OrGroup(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 1 ? list[0] : "{" + string.Join(" ", list) + "}";
    }

    private static string Label(string folder) => folder.Contains(' ') ? $"\"{folder}\"" : folder;
    private static string Quote(string subject) => subject.Contains(' ') ? $"\"{subject}\"" : subject;
}
