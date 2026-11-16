using System.Globalization;
using System.Text.Json;

using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkAccessAuditRingLossTests
{
    [Fact]
    public async Task QueueOverflow_WritesExactLossCountAndTimestampRange()
    {
        using var firstWriteStarted = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        string root = Path.Combine(Path.GetTempPath(), "nac-audit-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "decisions.jsonl");

        void BlockFirstWrite(NetworkAccessActivityEntry entry)
        {
            if (entry.Destination == "first")
            {
                firstWriteStarted.Set();
                releaseFirstWrite.Wait();
            }
        }

        var ring = new NetworkAccessAuditRing(path, 1024, 1, BlockFirstWrite);
        DateTimeOffset first = DateTimeOffset.UtcNow;
        try
        {
            ring.Record(CreateEntry(first, "first"));
            Assert.True(firstWriteStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            ring.Record(CreateEntry(first.AddTicks(1), "queued"));
            ring.Record(CreateEntry(first.AddTicks(2), "lost-1"));
            ring.Record(CreateEntry(first.AddTicks(3), "lost-2"));

            Task disposal = ring.DisposeAsync().AsTask();
            releaseFirstWrite.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            string log = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            int marker = log.IndexOf("\"decision\": \"audit_loss\"", StringComparison.Ordinal);
            Assert.NotEqual(-1, marker);
            int start = log.LastIndexOf('{', marker);
            int end = log.IndexOf('}', marker);
            using JsonDocument document = JsonDocument.Parse(log[start..(end + 1)]);
            string[] parts = document.RootElement.GetProperty("reason").GetString()!.Split(';');
            Assert.Equal("count=2", parts[0]);
            Assert.True(DateTimeOffset.TryParse(parts[1]["from=".Length..], CultureInfo.InvariantCulture, out DateTimeOffset from));
            Assert.True(DateTimeOffset.TryParse(parts[2]["to=".Length..], CultureInfo.InvariantCulture, out DateTimeOffset to));
            Assert.Equal(first.AddTicks(2), from);
            Assert.Equal(first.AddTicks(3), to);

            int countBefore = ring.List(new NetworkAccessActor("user", true)).Count;
            ring.Record(CreateEntry(first.AddTicks(4), "after-dispose"));
            Assert.Equal(countBefore, ring.List(new NetworkAccessActor("user", true)).Count);
        }
        finally
        {
            releaseFirstWrite.Set();
            await ring.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PendingLoss_IsWrittenWhenConsumerDrainsWithoutAnotherRecordOrShutdown()
    {
        using var firstWriteStarted = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        using var lossWritten = new ManualResetEventSlim();
        string root = Path.Combine(Path.GetTempPath(), "nac-audit-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var writeLock = new object();
        var written = new List<NetworkAccessActivityEntry>();

        void ObserveWrite(NetworkAccessActivityEntry entry)
        {
            if (entry.Destination == "first")
            {
                firstWriteStarted.Set();
                releaseFirstWrite.Wait();
            }

            lock (writeLock)
            {
                written.Add(entry);
            }

            if (entry.Decision == "audit_loss")
            {
                lossWritten.Set();
            }
        }

        var ring = new NetworkAccessAuditRing(Path.Combine(root, "decisions.jsonl"), 1024, 1, ObserveWrite);
        try
        {
            DateTimeOffset first = DateTimeOffset.UtcNow;
            ring.Record(CreateEntry(first, "first"));
            Assert.True(firstWriteStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            ring.Record(CreateEntry(first.AddTicks(1), "queued"));
            ring.Record(CreateEntry(first.AddTicks(2), "lost"));

            releaseFirstWrite.Set();
            Assert.True(lossWritten.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            lock (writeLock)
            {
                Assert.Single(written, entry => entry.Decision == "audit_loss");
            }
        }
        finally
        {
            releaseFirstWrite.Set();
            await ring.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static NetworkAccessActivityEntry CreateEntry(DateTimeOffset timestamp, string destination)
        => new(timestamp, null, "user", "agent", null, destination, 443, "denied", "test", null, false, false);
}
