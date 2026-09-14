namespace Vuelto.Shared.Ui.Components;

/// <summary>
/// The five transaction classes as the API spells them, plus the one place that decides how each one looks
/// and which resource names it. Shared.Ui has no reference to Core by design (the UI is a client of the API,
/// golden rule 2), so these mirror <c>Vuelto.Core.Budget.TransactionTypes</c> rather than importing it — the
/// same relationship every DTO in this project already has with its server-side shape.
///
/// Before SKIN-2 the class-to-label switch was copied into MonthDetail, MerchantMappings and Review. Keeping
/// the mapping here is what lets <see cref="ClassChip"/> be the only thing that knows a class has a colour:
/// a call site names the class and gets the right chip, and cannot pick a tone of its own.
/// </summary>
public static class TransactionClasses
{
    public const string Budgeted = "budgeted";
    public const string Extraordinary = "extraordinary";
    public const string UnplannedEssential = "unplanned_essential";
    public const string Inflow = "inflow";
    public const string EnvelopeContribution = "envelope_contribution";

    /// <summary>The resource key naming a class, or null when the value is not one of the five.</summary>
    public static string? LabelKey(string? txClass) => txClass switch
    {
        Budgeted => "Tx_Budgeted",
        Extraordinary => "Tx_Extraordinary",
        UnplannedEssential => "Tx_Unplanned",
        Inflow => "Tx_Inflow",
        EnvelopeContribution => "Tx_EnvelopeContribution",
        _ => null,
    };

    /// <summary>
    /// The chip tone for a class. Each of the five is distinct, and the two that carry meaning carry the
    /// SKIN-1 semantic tokens: an unplanned essential is the one class that asks to be looked at, so it is
    /// the amber "needs attention" — never red, which stays reserved for over-budget. Money in is green.
    /// The other three are neutral, because "planned" and "discretionary" are not states to act on.
    /// </summary>
    public static string Tone(string? txClass) => txClass switch
    {
        Budgeted => "plan",
        Extraordinary => "neutral",
        UnplannedEssential => "warn",
        Inflow => "good",
        EnvelopeContribution => "saving",
        _ => "neutral",
    };
}
