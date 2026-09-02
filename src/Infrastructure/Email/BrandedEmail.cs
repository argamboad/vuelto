using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Infrastructure.Email;

/// <summary>A rendered branded email: localized subject, HTML, and the inline images it references.</summary>
public sealed record EmailBody(string Subject, string Html, IReadOnlyList<EmailInlineImage> InlineImages);

/// <summary>
/// Builds the Perezosoft-branded HTML for transactional emails. Email HTML is its own
/// world — table-based layout, inline styles, web-safe fonts only — so this does NOT reuse
/// the app's CSS. The logo is embedded via CID (multipart/related), the one approach Gmail
/// and Outlook render reliably (they block data-URI images).
/// <para>
/// Localized per <paramref name="culture"/> from <c>EmailStrings.resx</c>: emails are sent
/// server-side in an explicit culture (the requester's UI language for OTP/magic link, the
/// inviter's saved locale for invites), so it uses <see cref="ResourceManager"/> keyed by
/// culture rather than the ambient thread culture.
/// </para>
/// </summary>
public static class BrandedEmail
{
    private const string LogoCid = "perezosoft-logo";

    // Brand palette (mirrors the app theme).
    private const string Green = "#465d4d";
    private const string GreenDark = "#2f3d33";
    private const string Sage = "#6b8a72";
    private const string SageLight = "#9bb6a1";
    private const string Surface = "#F5F7F4";
    private const string Border = "#DCE5DD";
    private const string Ink = "#33403a";
    private const string Muted = "#5a6b62";
    private const string Font = "'Segoe UI',Helvetica,Arial,sans-serif";

    private static readonly ResourceManager Rm =
        new("Perezosoft.Infrastructure.Email.EmailStrings", typeof(BrandedEmail).Assembly);
    private static readonly CultureInfo DefaultCulture = CultureInfo.GetCultureInfo("en");

    /// <summary>Resolves a locale code (e.g. "es") to a culture, defaulting to English.</summary>
    public static CultureInfo ResolveCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return DefaultCulture;
        try { return CultureInfo.GetCultureInfo(locale.Trim()); }
        catch (CultureNotFoundException) { return DefaultCulture; }
    }

    private static string T(string key, CultureInfo culture, params object[] args)
    {
        var value = Rm.GetString(key, culture) ?? key;
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }

    /// <summary>The logo as an inline image; reference it from HTML as <c>cid:perezosoft-logo</c>.</summary>
    public static EmailInlineImage Logo() => new(LogoCid, "logo.png", LoadLogo(), "image/png");

    /// <summary>"Email me a 6-digit code" — the OTP code email.</summary>
    public static EmailBody Otp(string code, int lifespanMinutes, CultureInfo culture) => Compose(
        T("Otp_Subject", culture),
        T("Otp_Preheader", culture, code),
        $"""
         {Heading(T("Otp_Heading", culture))}
         {Paragraph(T("Otp_Body", culture, lifespanMinutes))}
         <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:8px 0 4px;">
           <div style="display:inline-block;background:{Surface};border:1px solid {Border};border-radius:10px;padding:16px 26px;font-family:{Font};font-size:30px;font-weight:700;letter-spacing:.35em;color:{Green};">{code}</div>
         </td></tr></table>
         {IgnoreNote(T("Common_IgnoreNote", culture))}
         """);

    /// <summary>"Email me a magic link" — the passwordless sign-in link.</summary>
    public static EmailBody MagicLink(string link, int lifespanMinutes, CultureInfo culture) => Compose(
        T("MagicLink_Subject", culture),
        T("MagicLink_Preheader", culture),
        $"""
         {Heading(T("MagicLink_Heading", culture))}
         {Paragraph(T("MagicLink_Body", culture, lifespanMinutes))}
         {Button(T("MagicLink_Button", culture), link)}
         {Paragraph(T("MagicLink_OrPaste", culture), small: true)}
         <p style="margin:0 0 8px;font-family:{Font};font-size:12px;line-height:1.5;color:{Sage};word-break:break-all;">{link}</p>
         {IgnoreNote(T("Common_IgnoreNote", culture))}
         """);

    /// <summary>Household invitation — join link plus the raw token fallback.</summary>
    public static EmailBody Invitation(string joinUrl, string token, CultureInfo culture) => Compose(
        T("Invitation_Subject", culture),
        T("Invitation_Preheader", culture),
        $"""
         {Heading(T("Invitation_Heading", culture))}
         {Paragraph(T("Invitation_Body", culture))}
         {Button(T("Invitation_Button", culture), joinUrl)}
         {Paragraph(T("Invitation_OrToken", culture), small: true)}
         <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:0 0 4px;">
           <div style="display:inline-block;background:{Surface};border:1px solid {Border};border-radius:8px;padding:10px 16px;font-family:'Courier New',monospace;font-size:13px;color:{Green};word-break:break-all;">{token}</div>
         </td></tr></table>
         {IgnoreNote(T("Common_IgnoreNoteUnexpected", culture))}
         """);

    /// <summary>
    /// A branded in-app notification copy (NOTIFY-2). <paramref name="title"/>/<paramref name="body"/>
    /// are dynamic user-facing content, so they are HTML-encoded before interpolation — email content
    /// can't inject markup. The subject is the (plain) title.
    /// </summary>
    public static EmailBody Notification(string title, string body, CultureInfo culture) => Compose(
        title,
        WebUtility.HtmlEncode(title),
        $"""
         {Heading(WebUtility.HtmlEncode(title))}
         {Paragraph(WebUtility.HtmlEncode(body))}
         {IgnoreNote(T("Common_IgnoreNote", culture))}
         """);

    // ── shell + pieces ────────────────────────────────────────────────────────

    private static EmailBody Compose(string subject, string preheader, string inner) =>
        new(subject, Wrap(preheader, inner), [Logo()]);

    private static string Wrap(string preheader, string inner) => $"""
        <!DOCTYPE html>
        <html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="x-apple-disable-message-reformatting">
        <title>Perezosoft</title>
        </head>
        <body style="margin:0;padding:0;background:{Surface};">
        <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:{Surface};">{preheader}</div>
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{Surface};">
        <tr><td align="center" style="padding:32px 16px;">
          <table role="presentation" cellpadding="0" cellspacing="0" style="width:100%;max-width:480px;">
            <tr><td align="center" style="padding:4px 0 24px;">
              <img src="cid:{LogoCid}" width="56" height="56" alt="Perezosoft" style="display:block;border:0;outline:none;text-decoration:none;">
              <div style="font-family:{Font};font-size:20px;font-weight:700;letter-spacing:-.01em;color:{Green};margin-top:8px;">Perezosoft</div>
            </td></tr>
            <tr><td style="background:#ffffff;border:1px solid {Border};border-radius:14px;padding:36px 32px;">
              {inner}
            </td></tr>
            <tr><td align="center" style="padding:24px 8px 0;font-family:{Font};font-size:12px;line-height:1.6;color:{SageLight};">
              <div style="font-weight:600;color:{Sage};">Perezosoft</div>
              <div>Lazy reputation. Efficient engineering.</div>
            </td></tr>
          </table>
        </td></tr>
        </table>
        </body></html>
        """;

    private static string Heading(string text) =>
        $"""<h1 style="margin:0 0 10px;font-family:{Font};font-size:22px;font-weight:700;color:{GreenDark};">{text}</h1>""";

    private static string Paragraph(string text, bool small = false) =>
        $"""<p style="margin:0 0 {(small ? "8" : "24")}px;font-family:{Font};font-size:{(small ? "13" : "15")}px;line-height:1.6;color:{(small ? Muted : Ink)};">{text}</p>""";

    // Bulletproof-ish button (table + bgcolor) for Outlook compatibility.
    private static string Button(string label, string href) => $"""
        <table role="presentation" cellpadding="0" cellspacing="0" style="margin:4px auto 20px;"><tr>
          <td align="center" bgcolor="{Green}" style="border-radius:8px;">
            <a href="{href}" style="display:inline-block;padding:13px 34px;font-family:{Font};font-size:15px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:8px;">{label}</a>
          </td>
        </tr></table>
        """;

    private static string IgnoreNote(string text) =>
        $"""<p style="margin:24px 0 0;font-family:{Font};font-size:13px;line-height:1.5;color:{SageLight};">{text}</p>""";

    private static byte[]? _logoCache;
    private static byte[] LoadLogo()
    {
        if (_logoCache is not null) return _logoCache;
        var asm = typeof(BrandedEmail).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded email logo (logo.png) not found.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return _logoCache = memory.ToArray();
    }
}
