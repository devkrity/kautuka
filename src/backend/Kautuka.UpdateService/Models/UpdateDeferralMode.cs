using System.Text.Json.Serialization;

namespace Kautuka.UpdateService.Models;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateDeferralMode>))]
public enum UpdateDeferralMode
{
    None = 0,
    UntilNextRelease = 1,
    UntilDate = 2,
    UntilMaintenanceWindow = 3,
}
