using System.Diagnostics;

using OpenTelemetry;

namespace Kautuka.Telemetry.Telemetry;

internal sealed class KautukaIdentityBaggageProcessor : BaseProcessor<Activity>
{
    private static readonly string[] approvedIdentityKeys =
    [
        KautukaActivitySources.TenantIdAttribute,
        KautukaActivitySources.EndUserIdAttribute,
    ];

    public override void OnStart(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        foreach (string key in approvedIdentityKeys)
        {
            string? value = activity.GetBaggageItem(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                activity.SetTag(key, value);
            }
        }
    }
}
