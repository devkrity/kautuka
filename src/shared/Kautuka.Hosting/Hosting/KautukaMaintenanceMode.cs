using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

using System.Text.Json;

namespace Kautuka.Hosting.Hosting;

/// <summary>
/// Durable Linux appliance update fence. The root-owned helper writes the marker
/// before stopping any host process and removes it only after commit or verified
/// source recovery. App processes re-read it for each request so an already-running
/// Gateway closes admission as soon as maintenance begins.
/// </summary>
public static class KautukaMaintenanceMode
{
    public const string MarkerPath = "/var/lib/kautuka-recovery/maintenance.json";

    public static bool IsActive
        => OperatingSystem.IsLinux() && File.Exists(MarkerPath);

    // External effects must remain paused until the update is committed.
    // An unreadable marker always suppresses workers.
    public static bool ShouldSuppressWorkers(string? markerContents)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(markerContents ?? string.Empty);
            return document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("phase", out JsonElement phase)
                || phase.ValueKind != JsonValueKind.String
                || phase.GetString() is not ("committed" or "completed" or "aborted");
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool SuppressWorkersAtStartup()
    {
        if (!IsActive) return false;
        try { return ShouldSuppressWorkers(File.ReadAllText(MarkerPath)); }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static void SuppressApplicationHostedServices(
        IServiceCollection services,
        params Type[] allowedServices)
        => SuppressApplicationHostedServices(services, SuppressWorkersAtStartup(), allowedServices);

    public static void SuppressApplicationHostedServices(
        IServiceCollection services,
        bool maintenanceActive,
        params Type[] allowedServices)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!maintenanceActive)
        {
            return;
        }

        HashSet<Type> allowed = allowedServices.ToHashSet();
        ServiceDescriptor[] blocked = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Where(descriptor => descriptor.ImplementationType is null
                || (descriptor.ImplementationType.Namespace?.StartsWith("Kautuka.", StringComparison.Ordinal) ?? false))
            .Where(descriptor => descriptor.ImplementationType is null || !allowed.Contains(descriptor.ImplementationType))
            .ToArray();
        foreach (ServiceDescriptor descriptor in blocked)
        {
            services.Remove(descriptor);
        }
    }

    public static bool IsAllowedHealthRequest(PathString path)
        => path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/alive", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedGrpcRequest(PathString path, params string[] methodSuffixes)
        => methodSuffixes.Any(method => path.Value?.EndsWith("/" + method, StringComparison.Ordinal) == true);

    public static bool ShouldRunDesiredStateSideEffects(bool requiresReconciliation, bool maintenanceActive)
        => requiresReconciliation && !maintenanceActive;

    public static IApplicationBuilder UseAdmissionFence(
        this IApplicationBuilder app,
        Func<PathString, bool>? additionalAllowedPath = null)
        => UseAdmissionFence(app, static () => IsActive, path => IsAllowedHealthRequest(path)
            || (additionalAllowedPath?.Invoke(path) ?? false));

    public static IApplicationBuilder UseAdmissionFence(
        this IApplicationBuilder app,
        Func<bool> isMaintenanceActive,
        Func<PathString, bool> isAllowedPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(isMaintenanceActive);
        ArgumentNullException.ThrowIfNull(isAllowedPath);
        return app.Use(async (context, next) =>
        {
            if (isMaintenanceActive() && !isAllowedPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.CompleteAsync().ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }
}
