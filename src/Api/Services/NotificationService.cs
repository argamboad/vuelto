using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Email;

namespace Perezosoft.Api.Services;

/// <summary>A user's notification delivery preferences (NOTIFY-2). Absence ⇒ both on.</summary>
public sealed record NotificationPreferences(bool InApp, bool Email);

/// <summary>Notification kind conventions.</summary>
public static class NotificationKinds
{
    /// <summary>
    /// The <c>security.</c> namespace (e.g. <c>security.mfa_reset</c>) marks account-security events that a
    /// user must not be able to silence — a staff MFA reset, and any future password/email-change or
    /// new-device alert. These bypass delivery preferences and go to BOTH channels (v3 audit ADM-1): the
    /// out-of-band email is the point — an attacker who reached the account can't turn the alert off from
    /// inside the app. Everything else honors the user's prefs.
    /// </summary>
    public const string SecurityPrefix = "security.";

    public static bool IsSecurity(string kind) => kind.StartsWith(SecurityPrefix, StringComparison.Ordinal);
}

/// <summary>
/// Per-user in-app notifications (NOTIFY-1, ADR-013). <see cref="NotifyAsync"/> is the seam a feature
/// calls to notify a user; it <b>stages</b> the in-app row on the caller's unit of work (transactional
/// with the triggering change, like <c>IAuditLog</c>) and does not save. The read side (list, unread
/// count, mark read) is scoped to the caller — a user only ever sees/affects their own. In NOTIFY-1 the
/// in-app channel is the only one; the email channel + preferences arrive in NOTIFY-2.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Notifies a user across the channels their preferences allow (NOTIFY-2): the in-app row is
    /// staged on the caller's unit of work; the email copy goes through the outbox-backed
    /// <c>IEmailSender</c> (reliable, retried). Channels are never hard-coded here.
    /// </summary>
    Task NotifyAsync(Guid userId, string kind, string title, string body, object? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>The user's delivery preferences (defaults to both channels on).</summary>
    Task<NotificationPreferences> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SetPreferencesAsync(Guid userId, bool inApp, bool email, CancellationToken cancellationToken = default);

    /// <summary>The user's notifications, newest first; optionally before a cursor timestamp; capped at 100.</summary>
    Task<IReadOnlyList<Notification>> ListAsync(Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken = default);

    Task<int> UnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Marks one of the user's notifications read. False if it isn't theirs / doesn't exist.</summary>
    Task<bool> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes one of the user's notifications. False if it isn't theirs / doesn't exist.</summary>
    Task<bool> DeleteAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the user's notifications — only the already-read ones when <paramref name="onlyRead"/>
    /// is true, otherwise all of them. Returns the number of rows removed.</summary>
    Task<int> DeleteAllAsync(Guid userId, bool onlyRead, CancellationToken cancellationToken = default);
}

public sealed class NotificationService(
    IRepository<Notification> notifications,
    IRepository<NotificationPreference> preferences,
    IUserRepository users,
    IEmailSender emailSender,
    TimeProvider clock) : INotificationService
{
    public async Task NotifyAsync(Guid userId, string kind, string title, string body, object? metadata = null, CancellationToken cancellationToken = default)
    {
        var prefs = await GetPreferencesAsync(userId, cancellationToken);
        // security.* events are non-suppressible — a user must not be able to hide, e.g., a staff MFA reset
        // (ADM-1). Force both channels regardless of prefs; everything else honors them.
        var forceAll = NotificationKinds.IsSecurity(kind);

        if (prefs.InApp || forceAll)
            // Staged on the caller's unit of work — persists with the triggering change (ADR-013).
            await notifications.AddAsync(new Notification
            {
                UserId = userId,
                Kind = kind,
                Title = title,
                Body = body,
                Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
                CreatedAt = clock.GetUtcNow(),
            }, cancellationToken);

        if (prefs.Email || forceAll)
        {
            var user = await users.GetByIdAsync(userId, cancellationToken);
            if (user is not null)
            {
                // Brand the copy via the shared template (title/body are HTML-encoded inside). The
                // app-facing IEmailSender is the outbox-backed decorator (ADR-007) — reliable + retried.
                var emailBody = BrandedEmail.Notification(title, body, BrandedEmail.ResolveCulture(user.Locale));
                await emailSender.SendAsync(user.Email, emailBody.Subject, emailBody.Html, emailBody.InlineImages, cancellationToken);
            }
        }
    }

    public async Task<NotificationPreferences> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var row = await preferences.Query().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        return row is null
            ? new NotificationPreferences(InApp: true, Email: true) // default on
            : new NotificationPreferences(row.InAppEnabled, row.EmailEnabled);
    }

    public async Task SetPreferencesAsync(Guid userId, bool inApp, bool email, CancellationToken cancellationToken = default)
    {
        var row = await preferences.Query().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (row is null)
        {
            await preferences.AddAsync(new NotificationPreference { UserId = userId, InAppEnabled = inApp, EmailEnabled = email }, cancellationToken);
        }
        else
        {
            row.InAppEnabled = inApp;
            row.EmailEnabled = email;
            preferences.Update(row);
        }
        await preferences.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Notification>> ListAsync(Guid userId, DateTimeOffset? before, int limit, CancellationToken cancellationToken = default)
    {
        var query = notifications.Query().Where(n => n.UserId == userId);
        if (before is { } cursor)
            query = query.Where(n => n.CreatedAt < cursor);

        return await query
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
    }

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken cancellationToken = default) =>
        notifications.Query().CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);

    public async Task<bool> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var notification = await notifications.Query()
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, cancellationToken);
        if (notification is null)
            return false;

        if (notification.ReadAt is null)
        {
            notification.ReadAt = clock.GetUtcNow();
            notifications.Update(notification);
            await notifications.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return notifications.Query()
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default) =>
        await notifications.Query()
            .Where(n => n.Id == notificationId && n.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    public Task<int> DeleteAllAsync(Guid userId, bool onlyRead, CancellationToken cancellationToken = default)
    {
        // Per-user, not tenant-scoped (ADR-C2), so a UserId-filtered set delete is safe here — there is no
        // tenant filter/RLS to render for a set-based delete. onlyRead lets a client reclaim just what's
        // already been seen (retention-style) without dropping unread items the user hasn't looked at.
        var query = notifications.Query().Where(n => n.UserId == userId);
        if (onlyRead)
            query = query.Where(n => n.ReadAt != null);
        return query.ExecuteDeleteAsync(cancellationToken);
    }
}
