// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Infrastructure/Gateway/GatewaySessionFactory.cs | Namespace: Kautuka.Tui.Infrastructure.Gateway
using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;

namespace Kautuka.Tui.Infrastructure.Gateway;

public static class GatewaySessionFactory
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient ownership is transferred to GatewayApiClient and then to the caller.")]
    public static GatewayApiClient CreateClient(TuiCommandOptions? options = null)
    {
        HttpClient httpClient = new()
        {
            BaseAddress = options?.GatewayUrl ?? TuiCommandOptions.ResolveGatewayUrl(),
            Timeout = TimeSpan.FromSeconds(30),
        };

        GatewayApiClient client = new(httpClient);
        if (!string.IsNullOrWhiteSpace(options?.AccessToken))
        {
            client.SetAccessToken(options.AccessToken);
        }

        return client;
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient ownership is transferred to GatewayApiClient and then to the caller on success.")]
    public static async Task<Result<GatewayApiClient>> CreateAuthenticatedClientAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        GatewayApiClient? client = null;

        try
        {
            client = CreateClient(options);

            if (!string.IsNullOrWhiteSpace(options.AccessToken))
            {
                client.SetAccessToken(options.AccessToken);
                GatewayApiClient authenticatedClient = client;
                client = null;
                return Results.Success(authenticatedClient);
            }

            string? username = !string.IsNullOrWhiteSpace(options.Username)
                ? options.Username
                : ReadUsername();

            if (string.IsNullOrWhiteSpace(username))
            {
                return Results.Failure<GatewayApiClient>("Gateway authentication was cancelled.");
            }

            string validatedUsername = username;

            string password = !string.IsNullOrWhiteSpace(options.Password)
                ? options.Password
                : ReadPassword(validatedUsername);

            Result<AuthLoginResponse> loginResult = await client.LoginAsync(validatedUsername, password, cancellationToken).ConfigureAwait(false);
            if (!loginResult.IsSuccess || loginResult.Value is null)
            {
                return Results.Failure<GatewayApiClient>(loginResult.ErrorMessage ?? "Authentication failed.");
            }

            client.SetAccessToken(loginResult.Value.AccessToken);
            GatewayApiClient readyClient = client;
            client = null;
            return Results.Success(readyClient);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static string? ReadUsername()
    {
        Console.Write("Gateway username: ");
        return Console.ReadLine()?.Trim();
    }

    private static string ReadPassword(string username)
    {
        Console.Write($"Password for {username}: ");
        List<char> buffer = [];

        while (true)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                continue;
            }

            if (keyInfo.KeyChar != '\0')
            {
                buffer.Add(keyInfo.KeyChar);
            }
        }

        return new string([.. buffer]);
    }
}
