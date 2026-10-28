// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Logs/LogsCommand.cs | Namespace: Kautuka.Tui.Features.Logs
using Kautuka.Tui.Common;
using Kautuka.Tui.Features.Services;
using Kautuka.Tui.Infrastructure.Processes;

using Spectre.Console;

namespace Kautuka.Tui.Features.Logs;

public sealed class LogsCommand(ShellCommandRunner commandRunner)
{
    private readonly ShellCommandRunner commandRunner = commandRunner;

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return Results.Failure("Specify a service key or unit name. Example: kautuka-tui logs gateway --lines 80");
        }

        Result<ServiceUnitDefinition> serviceResult = SystemServiceCatalog.Resolve(arguments[0]);
        if (!serviceResult.IsSuccess || serviceResult.Value is null)
        {
            return Results.Failure(serviceResult.ErrorMessage ?? "Unknown service.");
        }

        int lineCount = ParseLineCount(arguments.Skip(1).ToArray());

        Result<ShellCommandResult> logResult = await commandRunner.RunAsync(
            "journalctl",
            ["-u", serviceResult.Value.UnitName, "--no-pager", "--output=short-iso", "-n", lineCount.ToString()],
            20,
            cancellationToken).ConfigureAwait(false);

        if (!logResult.IsSuccess || logResult.Value is null)
        {
            return Results.Failure(logResult.ErrorMessage ?? "Failed to load logs.");
        }

        if (!logResult.Value.IsSuccess)
        {
            return Results.Failure(logResult.Value.DescribeFailure($"Reading logs for {serviceResult.Value.UnitName}"));
        }

        AnsiConsole.Write(new Rule($"[bold]Logs: {Markup.Escape(serviceResult.Value.DisplayName)}[/]"));
        AnsiConsole.Write(new Text(logResult.Value.StdOut));
        return Results.Success();
    }

    private static int ParseLineCount(string[] arguments)
    {
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if ((arguments[index] == "--lines" || arguments[index] == "-n")
                && int.TryParse(arguments[index + 1], out int parsed)
                && parsed > 0)
            {
                return parsed;
            }
        }

        return 80;
    }
}
