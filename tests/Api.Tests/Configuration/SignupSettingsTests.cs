using Microsoft.Extensions.Configuration;
using Vuelto.Api.Configuration;

namespace Vuelto.Api.Tests.Configuration;

/// <summary>
/// v4 AUTH-7 (T35, R82): the green list is bound exactly as configured. An entry like <c>" friend@x.com"</c>
/// or <c>"@x.com"</c> silently admitted nobody, and uncommenting the example key with no value bound an empty
/// entry that switched the list ON while allowing no one — a locked deployment whose only hint was the
/// startup posture line. Every list entry is normalized at bind: trimmed, lower-cased, a leading <c>@</c>
/// stripped, blanks dropped. Reflective over the settings' list properties, so a new list gets the same rule.
/// </summary>
public class SignupSettingsTests
{
    public static TheoryData<string> ListProperties =>
        [.. typeof(SignupSettings).GetProperties().Where(p => p.PropertyType == typeof(string[])).Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(ListProperties))]
    public void SignupSettings_ListEntries_AreNormalizedAtBind(string property)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{SignupSettings.SectionName}:{property}:0"] = " X ",
            [$"{SignupSettings.SectionName}:{property}:1"] = "@d",
            [$"{SignupSettings.SectionName}:{property}:2"] = "",
            [$"{SignupSettings.SectionName}:{property}:3"] = "   ",
            [$"{SignupSettings.SectionName}:{property}:4"] = "Friend@Example.COM",
        }).Build();

        var settings = new SignupSettings();
        config.GetSection(SignupSettings.SectionName).Bind(settings);

        var values = (string[])typeof(SignupSettings).GetProperty(property)!.GetValue(settings)!;
        Assert.Equal(["x", "d", "friend@example.com"], values);
    }

    [Fact]
    public void ListProperties_AreKnown()
    {
        // The theory above is only as wide as this: a new list on the settings must appear here.
        var lists = typeof(SignupSettings).GetProperties().Where(p => p.PropertyType == typeof(string[])).Select(p => p.Name).Order();
        Assert.Equal(["AllowedDomains", "AllowedEmails"], lists);
    }

    [Fact]
    public void AnAllBlankList_IsNotAGreenList()
    {
        // Blank entries dropped ⇒ nothing configured ⇒ the open posture, which the startup line then states —
        // not a list that is "on" and admits no one.
        var settings = new SignupSettings { AllowedEmails = ["", "  "], AllowedDomains = [] };

        Assert.False(settings.IsRestricted);
    }

    [Fact]
    public void Allows_MatchesNormalizedEntries_CaseInsensitively()
    {
        var settings = new SignupSettings { AllowedEmails = [" Friend@Example.com "], AllowedDomains = ["@Team.example"] };

        Assert.True(settings.Allows("friend@example.com"));
        Assert.True(settings.Allows("anyone@team.example"));
        Assert.False(settings.Allows("anyone@notteam.example"));
    }
}
