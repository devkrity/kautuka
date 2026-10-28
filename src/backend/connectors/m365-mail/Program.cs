using System.Text;

namespace Kautuka.M365Mail;

internal static class Program
{
    public static async Task<int> Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        await MailMcpStdioServer.RunAsync(input, output, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }
}
