namespace SMS.Modules.Suppliers.Services;

// FSD Section 5.1 grade thresholds — shared by the recalculation service (which stores a snapshot's
// grade) and the dashboard (which re-derives a grade for an aggregated multi-snapshot window).
internal static class ScorecardGradeCalculator
{
    public static string GradeFor(decimal composite) => composite switch
    {
        >= 90m => "A",
        >= 80m => "B",
        >= 70m => "C",
        >= 60m => "D",
        _      => "F"
    };
}
