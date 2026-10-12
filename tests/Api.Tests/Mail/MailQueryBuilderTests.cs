using Vuelto.Core.Mail;
using Vuelto.Infrastructure.Mail;

namespace Vuelto.Api.Tests.Mail;

/// <summary>EMAIL-3 (donor US-027 AC2): every filter is pushed into the provider query, not applied after download.</summary>
public class MailQueryBuilderTests
{
    private static readonly DateTimeOffset After = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);

    private static EmailQuery Query(bool unreadOnly = true, string[]? folders = null) => new(
        Folders: folders ?? ["Inbox"],
        Senders: ["notificacion@notificacionesbaccr.com", "bncontacto@bncr.fi.cr"],
        Subjects: ["Notificación de transacción", "Voucher Digital"],
        ReceivedAfter: After, UnreadOnly: unreadOnly, MaxResults: 50);

    [Fact]
    public void Graph_Filter_EncodesReceivedUnreadSenderAndSubject()
    {
        var f = GraphQueryBuilder.BuildFilter(Query());
        Assert.Contains("receivedDateTime ge 2026-06-16T12:00:00Z", f);
        Assert.Contains("isRead eq false", f);
        Assert.Contains("from/emailAddress/address eq 'notificacion@notificacionesbaccr.com'", f);
        Assert.Contains("startswith(subject,'Voucher Digital')", f);
        Assert.Contains(" or ", f);
        Assert.Contains(" and (", f);
    }

    [Fact]
    public void Graph_Filter_OmitsIsRead_WhenNotUnreadOnly() => Assert.DoesNotContain("isRead", GraphQueryBuilder.BuildFilter(Query(unreadOnly: false)));

    [Fact]
    public void Graph_Url_ScopesToTheFolder_SelectsLeastFields_NoOrderBy()
    {
        var url = GraphQueryBuilder.MessagesUrl("Inbox", Query());
        Assert.StartsWith("/me/mailFolders/Inbox/messages?", url);
        Assert.Contains("$top=50", url);
        Assert.Contains(Uri.EscapeDataString("id,subject,from,receivedDateTime,body"), url);
        Assert.Contains("$filter=", url);
        Assert.DoesNotContain("$orderby", url);
        Assert.StartsWith("/me/messages?", GraphQueryBuilder.MessagesUrl(null, Query()));
    }

    [Fact]
    public void Graph_Filter_EscapesSingleQuotes()
    {
        var q = Query() with { Subjects = ["O'Brien"], Senders = [] };
        Assert.Contains("startswith(subject,'O''Brien')", GraphQueryBuilder.BuildFilter(q));
    }

    [Fact]
    public void Gmail_Q_EncodesFolderSenderSubjectAfterAndUnread()
    {
        var q = GmailQueryBuilder.BuildQ(Query());
        Assert.Contains("label:inbox", q.ToLowerInvariant());
        Assert.Contains("from:(notificacion@notificacionesbaccr.com OR bncontacto@bncr.fi.cr)", q);
        Assert.Contains("subject:(\"Notificación de transacción\" OR \"Voucher Digital\")", q);
        Assert.Contains($"after:{After.ToUnixTimeSeconds()}", q);
        Assert.Contains("{from:(", q); // sender OR subject as one brace group
        Assert.EndsWith("is:unread", q);
    }

    [Fact]
    public void Gmail_Q_DefaultsToInbox_AndOmitsUnread_WhenNotUnreadOnly()
    {
        var q = GmailQueryBuilder.BuildQ(Query(unreadOnly: false, folders: []));
        Assert.StartsWith("in:inbox ", q);
        Assert.DoesNotContain("is:unread", q);
    }
}
