using Microsoft.Extensions.Configuration;
using Vuelto.Infrastructure.Email;

namespace Vuelto.Api.Tests;

/// <summary>
/// Security posture of the SMTP TLS options (QA fix slice, 2026-07-06 Apple pass): revocation
/// checking must stay ON unless a dev box explicitly opts out — the knob exists only because
/// macOS trust evaluation fails some relays (e.g. Brevo) with "incomplete certificate
/// revocation check", which MailKit treats as fatal. R21 posture: absent config = secure.
/// </summary>
public class SmtpSettingsTests
{
    [Fact]
    public void CheckCertificateRevocation_DefaultsOn()
    {
        var settings = new SmtpSettings { Host = "smtp.example.test", FromAddress = "noreply@example.test" };

        Assert.True(settings.CheckCertificateRevocation);
    }

    [Fact]
    public void CheckCertificateRevocation_BindsFromConfig()
    {
        // The documented .env override (Email__Smtp__CheckCertificateRevocation=false) must reach
        // the bound settings — the same "Email:Smtp" section ServiceCollectionExtensions binds.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Smtp:Host"] = "smtp.example.test",
            ["Email:Smtp:FromAddress"] = "noreply@example.test",
            ["Email:Smtp:CheckCertificateRevocation"] = "false",
        }).Build();

        var settings = config.GetSection("Email:Smtp").Get<SmtpSettings>();

        Assert.NotNull(settings);
        Assert.False(settings.CheckCertificateRevocation);
    }
}
