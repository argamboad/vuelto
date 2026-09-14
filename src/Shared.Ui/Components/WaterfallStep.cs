using Microsoft.AspNetCore.Components;

namespace Vuelto.Shared.Ui.Components;

/// <summary>
/// One step of the four-step month summary. <paramref name="Op"/> is the arithmetic sign shown before the
/// label ("", "−", "="), which is what makes the row read as a calculation rather than four unrelated
/// figures — the same arithmetic, and the same order, as the eleven-row waterfall it replaces.
/// </summary>
public sealed record WaterfallStep(string Op, string Label, RenderFragment Value, string? Sub = null, string Tone = "neutral");
