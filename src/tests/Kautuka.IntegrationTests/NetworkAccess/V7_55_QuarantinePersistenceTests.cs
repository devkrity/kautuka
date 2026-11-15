using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_QuarantinePersistenceTests
{
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
            Assert.ThrowsAny<IOException>(() => store.QuarantineWorkload("container-id"));
            File.Delete(blocker);
            store.QuarantineWorkload("container-id");
            var reopened = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            Assert.True(reopened.IsWorkloadQuarantined("container-id"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
