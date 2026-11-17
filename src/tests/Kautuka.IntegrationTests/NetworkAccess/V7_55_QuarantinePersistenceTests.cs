using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_QuarantinePersistenceTests
{
    [Fact]
    public void ReopenedQuarantineIgnoresReusableNamesButRetainsImmutableIds()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string statePath = Path.Combine(temp, "manager-desired-state.json");
        const string containerName = "connector-reused-name";
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        try
        {
            var oldStore = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            oldStore.QuarantineWorkload(containerName);
            oldStore.QuarantineWorkload(containerId);

            var reopened = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            Assert.False(reopened.IsWorkloadQuarantined(containerName));
            Assert.True(reopened.IsWorkloadQuarantined(containerId));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void QuarantinePersistenceCanRetryAfterAtomicWriteFailure()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string blocker = Path.Combine(temp, "blocked");
        File.WriteAllText(blocker, "block");
        string statePath = Path.Combine(blocker, "manager-desired-state.json");
        var store = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
        try
        {
            const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            Assert.ThrowsAny<IOException>(() => store.QuarantineWorkload(containerId));
            File.Delete(blocker);
            store.QuarantineWorkload(containerId);
            var reopened = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            Assert.True(reopened.IsWorkloadQuarantined(containerId));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
