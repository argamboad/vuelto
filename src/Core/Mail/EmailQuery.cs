using Vuelto.Core.Entities;

namespace Vuelto.Core.Mail;

/// <summary>
/// Provider-independent description of which messages to fetch (EMAIL-3). Built from an
/// <see cref="EmailConnection"/>; the provider query builders translate it into a Graph <c>$filter</c>
/// or a Gmail <c>q</c>. Encodes the "don't flood on history" rule: only mail received on/after the
/// cursor (with a small look-back — re-seen messages are deduped downstream), and only unread when set.
/// </summary>
public record EmailQuery(
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Senders,
    IReadOnlyList<string> Subjects,
    DateTimeOffset ReceivedAfter,
    bool UnreadOnly,
    int MaxResults)
{
    /// <summary>Per-page cap (least data, bounded fetch).</summary>
    public const int DefaultMaxResults = 50;

    /// <summary>Look-back so a message arriving right on the cursor boundary isn't missed.</summary>
    public static readonly TimeSpan CursorOverlap = TimeSpan.FromMinutes(5);

    public static EmailQuery From(EmailConnection c, int maxResults = DefaultMaxResults)
    {
        var cursor = (c.LastPolledAt ?? c.ImportFrom).ToUniversalTime();
        return new EmailQuery(
            Folders: c.Folders,
            Senders: c.SenderFilters,
            Subjects: c.SubjectFilters,
            ReceivedAfter: c.IgnoreCursor ? DateTimeOffset.UnixEpoch : cursor - CursorOverlap,
            UnreadOnly: c.UnreadOnly,
            MaxResults: maxResults);
    }
}
