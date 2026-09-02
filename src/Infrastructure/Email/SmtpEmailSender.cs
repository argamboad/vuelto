using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Infrastructure.Email;

public class SmtpEmailSender(IOptions<SmtpSettings> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpSettings _settings = options.Value;

    public async Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlBody };
        if (inlineImages is not null)
        {
            foreach (var image in inlineImages)
            {
                var resource = builder.LinkedResources.Add(
                    image.FileName, image.Content, ContentType.Parse(image.MediaType));
                resource.ContentId = image.ContentId; // referenced from HTML as cid:{ContentId}
            }
        }
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient
        {
            // Bound a hung server: without this, a stuck Connect/Send stalls the request forever.
            Timeout = _settings.TimeoutSeconds * 1000,
            CheckCertificateRevocation = _settings.CheckCertificateRevocation,
        };
        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, SecureSocketOptions.Auto, cancellationToken);
            if (!string.IsNullOrEmpty(_settings.Username))
                await client.AuthenticateAsync(_settings.Username, _settings.Password ?? string.Empty, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // genuine cancellation — let it propagate, don't mask as a send failure
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP send to {Host}:{Port} failed", _settings.Host, _settings.Port);
            throw new EmailSendException($"Failed to send email via {_settings.Host}:{_settings.Port}.", ex);
        }
    }
}
