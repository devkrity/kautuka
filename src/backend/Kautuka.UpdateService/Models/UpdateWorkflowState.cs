using System.Text.Json.Serialization;

namespace Kautuka.UpdateService.Models;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateWorkflowState>))]
public enum UpdateWorkflowState
{
    Current = 0,
    Available = 1,
    Deferred = 2,
    Unsupported = 3,
    Staging = 4,
    Staged = 5,
    Applying = 6,
    VerificationPending = 7,
    VerificationFailed = 8,
    RevertAvailable = 9,
    Applied = 10,
}
