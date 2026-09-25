using System.Reflection;
using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Configuration;

/// <summary>
/// The app's settings classes, found by reflection, for the gates that must see every one of them: the config
/// catalog (<c>DocAndConfigSyncTests</c>, v4 T6) and the empty-config posture (<see cref="ConfigPostureTests"/>, v4 T7).
/// A class named <c>*Settings</c> is <b>section-bound</b> when it declares <c>public const string SectionName</c>;
/// the <c>SettingsProvider</c> classes instead implement a Core <c>I*Settings</c> interface and are built key by key.
/// </summary>
public static class SettingsCatalog
{
    public static IEnumerable<Type> All() =>
        new[] { typeof(global::Vuelto.Api.Configuration.BillingSettings).Assembly,
                typeof(global::Vuelto.Infrastructure.Email.SmtpSettings).Assembly,
                typeof(global::Vuelto.Core.Abstractions.IEmailSender).Assembly }
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && t.Name.EndsWith("Settings", StringComparison.Ordinal));

    public static IEnumerable<Type> SectionBound() => All().Where(t => SectionNameOf(t) is not null);

    /// <summary>
    /// The settings classes bound from configuration: those that declare a SectionName, plus any the source uses as
    /// options (<c>Configure&lt;T&gt;</c>, <c>AddOptions&lt;T&gt;</c>, <c>IOptions*&lt;T&gt;</c>, <c>.Get&lt;T&gt;()</c>).
    /// The name alone is not enough: an app's entities and EF migrations end in "Settings" too (BudgetSettings,
    /// AddBudgetSettings), and they are not config.
    /// </summary>
    public static IEnumerable<Type> ConfigBound()
    {
        var used = OptionsTypeNames();
        return All().Where(t => SectionNameOf(t) is not null || used.Contains(t.Name));
    }

    private static HashSet<string> OptionsTypeNames()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
            dir = dir.Parent;
        var src = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "src");
        var use = new Regex(@"(?:Configure|AddOptions|IOptions|IOptionsMonitor|IOptionsSnapshot|\.Get)<(\w+)>");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => use.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string? SectionNameOf(Type t) =>
        t.GetField("SectionName", BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } f
            ? (string?)f.GetRawConstantValue()
            : null;

    public static bool IsBuiltKeyByKey(Type t) =>
        t.GetInterfaces().Any(i => i.Name.StartsWith('I') && i.Name.EndsWith("Settings", StringComparison.Ordinal));

    /// <summary>A section-bound class with a <c>bool Enabled</c>: a feature switch.</summary>
    public static bool HasEnabledSwitch(Type t) => t.GetProperty("Enabled")?.PropertyType == typeof(bool);
}
