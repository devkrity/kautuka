// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Runtime/RuntimeCommand.cs | Namespace: Kautuka.Tui.Features.Runtime
using Kautuka.Tui.Cli;

using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Runtime;

public sealed class RuntimeCommand(RuntimeInspector inspector)
{
    private readonly RuntimeInspector inspector = inspector;

    public Task<Result<RuntimeSummary>> GetSummaryAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
        => inspector.GetAsync(options, cancellationToken);

    public Task<Result<PackageCacheSummary>> GetPackageCacheAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
        => inspector.GetPackageCacheAsync(options, cancellationToken);

    public static IRenderable BuildRuntimeRenderable(RuntimeSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!summary.IsAvailable)
        {
            return new Panel($"[yellow]{Markup.Escape(summary.Summary)}[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Runtime", Justify.Left),
            };
        }

        if (summary.Containers.Count == 0)
        {
            return new Panel($"[bold]{Markup.Escape(summary.Summary)}[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Runtime", Justify.Left),
            };
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Name");
        table.AddColumn("Image");
        table.AddColumn("Status");

        foreach (ContainerStatus container in summary.Containers)
        {
            table.AddStripedRow(container.Name, container.Image, container.Status);
        }

        return new Rows(
            new Panel($"[bold]{Markup.Escape(summary.Summary)}[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Runtime", Justify.Left),
            },
            table);
    }

    public static IRenderable BuildPackageCacheRenderable(PackageCacheSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!summary.Ok)
        {
            return new Panel($"[yellow]{Markup.Escape(summary.Message)}[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Package cache", Justify.Left),
            };
        }

        Table totals = StripedTable.CreateRounded();
        totals.AddColumn("Field");
        totals.AddColumn("Value");
        totals.AddStripedRow("Packages", summary.Packages.ToString());
        totals.AddStripedRow("Versions", summary.Versions.ToString());
        totals.AddStripedRow("Artifacts", summary.Artifacts.ToString());
        totals.AddStripedRow("Cache size", FormatBytes(summary.TotalSizeBytes));
        totals.AddStripedRow("Total hits", summary.TotalHits.ToString());
        totals.AddStripedRow(
            "Ecosystems",
            summary.Ecosystems.Count == 0
                ? "-"
                : string.Join(", ", summary.Ecosystems.Select(static pair => $"{pair.Key}={pair.Value}")));

        List<IRenderable> rows =
        [
            new Panel($"[bold]{Markup.Escape(summary.Message)}[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Package cache", Justify.Left),
            },
            totals,
        ];

        if (summary.Popular.Count > 0)
        {
            Table popular = StripedTable.CreateRounded();
            popular.AddColumn("Ecosystem");
            popular.AddColumn("Package");
            popular.AddColumn("Hits");
            popular.AddColumn("Size");
            foreach (GatewayPkgsProxyPopularPackage package in summary.Popular)
            {
                popular.AddStripedRow(package.Ecosystem, package.Name, package.Hits.ToString(), FormatBytes(package.SizeBytes));
            }

            rows.Add(new Panel(popular)
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Popular", Justify.Left),
            });
        }

        if (summary.Recent.Count > 0)
        {
            Table recent = StripedTable.CreateRounded();
            recent.AddColumn("Ecosystem");
            recent.AddColumn("Package");
            recent.AddColumn("Cached at");
            recent.AddColumn("Size");
            foreach (GatewayPkgsProxyRecentPackage package in summary.Recent)
            {
                string name = string.IsNullOrWhiteSpace(package.Version) ? package.Name : $"{package.Name}@{package.Version}";
                recent.AddStripedRow(
                    package.Ecosystem,
                    name,
                    string.IsNullOrWhiteSpace(package.CachedAt) ? "-" : package.CachedAt,
                    FormatBytes(package.SizeBytes));
            }

            rows.Add(new Panel(recent)
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Recently cached", Justify.Left),
            });
        }

        return new Rows(rows);
    }

    public async Task<Result> ExecuteAsync(
        TuiCommandOptions? options,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string action = arguments.Count == 0 ? "containers" : arguments[0].ToLowerInvariant();
        return action switch
        {
            "package-cache" or "pkgs-cache" => await ExecutePackageCacheAsync(options, cancellationToken).ConfigureAwait(false),
            "containers" => await ExecuteContainersAsync(options, cancellationToken).ConfigureAwait(false),
            _ => Results.Failure("Unknown runtime action. Use 'runtime containers' or 'runtime package-cache'."),
        };
    }

    private async Task<Result> ExecuteContainersAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
    {
        Result<RuntimeSummary> runtimeResult = await inspector.GetAsync(options, cancellationToken).ConfigureAwait(false);
        if (!runtimeResult.IsSuccess || runtimeResult.Value is null)
        {
            return Results.Failure(runtimeResult.ErrorMessage ?? "Failed to inspect runtime state.");
        }

        AnsiConsole.Write(BuildRuntimeRenderable(runtimeResult.Value));
        return Results.Success();
    }

    private async Task<Result> ExecutePackageCacheAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
    {
        Result<PackageCacheSummary> cacheResult = await inspector.GetPackageCacheAsync(options, cancellationToken).ConfigureAwait(false);
        if (!cacheResult.IsSuccess || cacheResult.Value is null)
        {
            return Results.Failure(cacheResult.ErrorMessage ?? "Failed to read package proxy cache stats.");
        }

        AnsiConsole.Write(BuildPackageCacheRenderable(cacheResult.Value));
        return Results.Success();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes / 1024d;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        string format = "0.0";
        return $"{value.ToString(format)} {units[unitIndex]}";
    }
}
