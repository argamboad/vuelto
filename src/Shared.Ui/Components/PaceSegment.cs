namespace Vuelto.Shared.Ui.Components;

/// <summary>
/// One filled run of a <see cref="PaceBar"/>. <paramref name="Tone"/> names a colour role the bar knows
/// (primary · warn · good · bad · rail) rather than a colour, so a call site cannot invent one.
/// <paramref name="Hatched"/> has exactly one meaning everywhere it appears: <em>planned, not yet spent</em>.
/// </summary>
public sealed record PaceSegment(string Label, decimal Value, string Tone, bool Hatched = false);
