using System.Text;

namespace Kautuka.GoogleMail;

internal static class Program
{
    public static async Task<int> Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        await GmailMcpStdioServer.RunAsync(input, output, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }
}
