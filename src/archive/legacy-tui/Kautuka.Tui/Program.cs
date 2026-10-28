// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Program.cs | Namespace: Kautuka.Tui
using Kautuka.Tui.Cli;

namespace Kautuka.Tui;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellationTokenSource = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        TuiApplication application = TuiApplication.CreateDefault();
        return await application.RunAsync(args, cancellationTokenSource.Token).ConfigureAwait(false);
    }
}
