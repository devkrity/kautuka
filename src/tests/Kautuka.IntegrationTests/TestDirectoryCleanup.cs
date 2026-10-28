namespace Kautuka.IntegrationTests;

internal static class TestDirectoryCleanup
{
    public static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        const int maxAttempts = 10;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                {
                    // Best-effort only: on Windows, SQLite/handle races can leave temp trees
                    // locked. Swallow so dispose flakiness does not fail green test bodies.
                    return;
                }

                await DelayBeforeRetryAsync(attempt).ConfigureAwait(false);
            }
        }
    }

    private static async Task DelayBeforeRetryAsync(int attempt)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        int delayMilliseconds = 100 * attempt;
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
    }
}
