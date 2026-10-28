using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Sandbox;

public static class WorkspaceContainerSpecFingerprint
{
    public static string Compute(ContainerContext context, IReadOnlyList<string> connectorIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connectorIds);
        var builder = new StringBuilder();
        Append(builder, context.ScopeType);
        Append(builder, context.ScopeId);
        Append(builder, context.UserId ?? string.Empty);
        Append(builder, Path.GetFullPath(context.HostWorkspacePath));
        Append(builder, context.ContainerWorkspacePath);
        Append(builder, context.Image);
        Append(builder, context.DefaultCpuLimit.ToString(CultureInfo.InvariantCulture));
        Append(builder, context.DefaultMemoryLimit);
        Append(builder, context.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        // BaseCapabilities name agent features such as run_shell. They are not Linux
        // capabilities and are not passed to the container, so they do not belong in this hash.

        foreach ((string key, string value) in context.ContainerEnvironment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, key);
            Append(builder, value);
        }

        foreach (string connectorId in connectorIds.Order(StringComparer.Ordinal))
        {
            Append(builder, connectorId);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
}
