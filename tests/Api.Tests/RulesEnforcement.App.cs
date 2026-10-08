namespace Vuelto.Api.Tests;

/// <summary>
/// The app's half of the enforcement manifest (Arch A1, R159). <c>RulesEnforcement.cs</c> lists, per machine rule,
/// the platform's standing check and is identical across repos; what differs per app — a rule whose check is still
/// owed here (<c>Pending</c>, with this repo's issue) or a rule about something that does not exist in this repo
/// (<c>NotHere</c>, with where it lives instead) — is said here, keyed by rule. The gate
/// (<c>EveryMachineRule_NamesAStandingCheck</c>) applies an override on top of the platform's entry. Before this
/// split the per-app fields lived on the platform's lines, and every port of the manifest was a hand merge.
/// </summary>
public static partial class RulesEnforcement
{
    /// <param name="Pending">When part of the mechanism is still unbuilt here: the owning issue (<c>repo#n</c>) and what is missing.</param>
    /// <param name="NotHere">When the thing the rule guards does not exist in this repo: where it lives instead.</param>
    public sealed record AppOverride(string? Pending = null, string? NotHere = null);

    /// <summary>Per rule, this repo's departure from the platform's entry.</summary>
    public static readonly IReadOnlyDictionary<string, AppOverride> AppOverrides = new Dictionary<string, AppOverride>
    {
        ["R149"] = new(Pending: "vuelto#198 (the test-id sweep of this app's UI under the A12 amendment; the gate is skipped until it lands)"),
        ["R114"] = new(NotHere: "the course (docs/tutorial) lives in perezosoft-platform; its gates run there"),
        ["R115"] = new(NotHere: "the course (docs/tutorial) lives in perezosoft-platform; its gates run there"),
        ["R118"] = new(NotHere: "the rule's course-reconcile half has nothing to hold here: the course lives in perezosoft-platform"),
    };
}
