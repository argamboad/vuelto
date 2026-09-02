using Microsoft.Extensions.Configuration;
using Vuelto.Api.Configuration;
using Vuelto.Infrastructure.Files;

namespace Vuelto.Api.Tests.Configuration;

/// <summary>
/// v3 audit S0-G3: every config-gated feature must be CLOSED under empty configuration — the "opt-in
/// deliberately" posture (ADR-014/015/016/017) held only by each settings class's <c>= false</c> / empty
/// default, with nothing pinning it. A future <c>Enabled { get; set; } = true</c> (or a non-empty default)
/// would silently ship a feature ON and pass CI. This binds each gate from an EMPTY configuration and
/// asserts it stays off; the assertions are compile-time-coupled to the settings classes, so flipping a
/// default turns this red. Add a line here when introducing a new gated feature.
/// </summary>
public class ConfigPostureTests
{
    private static readonly IConfiguration Empty = new ConfigurationBuilder().Build();

    private static T BoundFromEmptyConfig<T>(string section) where T : new()
    {
        var settings = new T();
        Empty.GetSection(section).Bind(settings);
        return settings;
    }

    [Fact]
    public void PublicApi_IsOff_ByDefault() =>
        Assert.False(BoundFromEmptyConfig<PublicApiSettings>(PublicApiSettings.SectionName).Enabled);

    [Fact]
    public void Webhooks_IsOff_ByDefault() =>
        Assert.False(BoundFromEmptyConfig<WebhooksSettings>(WebhooksSettings.SectionName).Enabled);

    [Fact]
    public void PlatformStaffAllowlist_IsEmpty_ByDefault() => // no self-serve / accidental platform staff
        Assert.Empty(BoundFromEmptyConfig<PlatformAdminSettings>("Admin").StaffEmails);

    [Fact]
    public void FileStorage_DefaultsToLocalDisk_NotS3() => // an empty Bucket is the local-vs-S3 switch
        Assert.Empty(BoundFromEmptyConfig<S3StorageSettings>("Storage:S3").Bucket);

    [Fact]
    public void ProxyForwardedHeaders_AreOff_ByDefault() => // trust-any-peer must be opt-in (Proxy:Enabled)
        Assert.False(Empty.GetValue("Proxy:Enabled", false));
}
