namespace Kautuka.UpdateService.Models;

public enum UpdateEligibilityReason
{
    None = 0,
    AlreadyCurrent = 1,
    NewerStableRequired = 2,
    NoNewerPreview = 3,
    ComparisonError = 4,
    UntrustedSourceUrl = 5,
}
