using System.Text;

using Kautuka.Hosting.Hosting;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal sealed class PublicSuffixRefreshWorker(
    IHttpClientFactory clients,
    UpdateServiceRuntimePaths paths,
    IOptions<UpdateOptions> options,
    ILogger<PublicSuffixRefreshWorker> logger) : BackgroundService
{
    internal const string ClientName = "kautuka.updateservice.publicsuffix";
    private static readonly Uri source = new("https://publicsuffix.org/list/public_suffix_list.dat");
    private static readonly UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly TimeSpan retryInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UpdateOptions settings = options.Value;
        if (!settings.Enabled) return;

        string path = Path.Combine(paths.RuntimeRoot, NetworkRegistrableDomain.ListFileName);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = retryInterval;
            try
            {
                if (!KautukaMaintenanceMode.IsActive)
                {
                    int currentCount = ReadRuleCount(path);
                    TimeSpan remaining = currentCount < settings.PublicSuffixMinimumRuleCount
                        ? TimeSpan.Zero
                        : settings.PublicSuffixRefreshInterval - (DateTime.UtcNow - File.GetLastWriteTimeUtc(path));
                    if (remaining <= TimeSpan.Zero)
                    {
                        using HttpClient client = clients.CreateClient(ClientName);
                        if (!await TryRefreshAsync(client, path, currentCount, settings, stoppingToken).ConfigureAwait(false))
                            logger.LogWarning("The downloaded Public Suffix List failed validation. Keeping the existing list.");
                    }
                    else if (remaining < delay)
                    {
                        delay = remaining;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or DecoderFallbackException
                || (ex is OperationCanceledException && !stoppingToken.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Public Suffix List refresh failed. Keeping the existing list.");
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private static int ReadRuleCount(string path)
    {
        try
        {
            using var reader = new StreamReader(path, utf8, detectEncodingFromByteOrderMarks: false);
            return NetworkRegistrableDomain.CountRules(reader);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DecoderFallbackException)
        {
            return 0;
        }
    }

    internal static async Task<bool> TryRefreshAsync(
        HttpClient client, string path, int currentCount, UpdateOptions options, CancellationToken cancellationToken)
    {
        string contents = utf8.GetString(await client.GetByteArrayAsync(source, cancellationToken).ConfigureAwait(false));
        using var reader = new StringReader(contents);
        int candidateCount = NetworkRegistrableDomain.CountRules(reader);
        if (candidateCount < options.PublicSuffixMinimumRuleCount
            || candidateCount * 100L < currentCount * (100L - options.PublicSuffixMaxRuleDropPercent))
            return false;

        if (KautukaMaintenanceMode.IsActive)
            return true;

        string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staged, contents, utf8, cancellationToken).ConfigureAwait(false);
            File.Move(staged, path, overwrite: true);
            return true;
        }
        finally
        {
            File.Delete(staged);
        }
    }
}
