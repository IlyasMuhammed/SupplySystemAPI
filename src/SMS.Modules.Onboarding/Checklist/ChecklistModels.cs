namespace SMS.Modules.Onboarding.Checklist;

// A50 D-28 / FSD §4.4 OnboardingStatus: the checklist API shape (docs/onboarding/SAMPLE-DATA.md §1.2).

public sealed class OnboardingChecklistModel
{
    public bool Dismissed { get; set; }
    public DateTime? DismissedAt { get; set; }
    public int CompletedCount { get; set; }
    public int TotalCount { get; set; }
    public bool IsComplete { get; set; }
    public List<OnboardingChecklistStepModel> Steps { get; set; } = new();
}

public sealed class OnboardingChecklistStepModel
{
    public string StepCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Link { get; set; }
    public int SortOrder { get; set; }
    public string? RequiresModule { get; set; }
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool AutoCompleted { get; set; }
}
