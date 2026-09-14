namespace Vuelto.Shared.Ui.Components;

/// <summary>One choice in a <see cref="SegmentedSwitch"/>: the value the caller stores, and the label a user reads.</summary>
public sealed record SegmentedOption(string Value, string Label);
