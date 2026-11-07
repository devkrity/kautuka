using System.Text.Json.Nodes;

using Kautuka.Manager.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class CoreServiceUpdateCommandTests
{
    private const string operation = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void RecordedSemverBuildMetadataIsAValidInstalledReleaseDirectory()
    {
        JsonObject record = Record();
        record["targetRelease"] = "1.0.1+build.42";
        string json = record.ToJsonString();
        Assert.Equal("1.0.1+build.42", CoreServiceUpdateCommand.ValidateOperation(operation, json, json,
            "/opt/kautuka/manager/versions/1.0.1+build.42/kautuka-manager"));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.1")]
    public void StopAcceptsOnlyTheRecordedSourceOrTargetExecutable(string release)
    {
        string record = Record().ToJsonString();
        Assert.Equal(release, CoreServiceUpdateCommand.ValidateOperation(operation, record, record,
            "/opt/kautuka/manager/versions/" + release + "/kautuka-manager"));
    }

    [Theory]
    [InlineData("/opt/kautuka/manager/current/kautuka-manager")]
    [InlineData("/opt/kautuka/manager/versions/1.0.2/kautuka-manager")]
    [InlineData("/opt/kautuka/manager/versions/1.0.0/other")]
    public void MutablePointersAndUnrelatedExecutablesAreRefused(string executable)
    {
        string record = Record().ToJsonString();
        Assert.Throws<InvalidOperationException>(() => CoreServiceUpdateCommand.ValidateOperation(operation, record, record, executable));
    }

    [Theory]
    [InlineData("phase", "restoring")]
    [InlineData("operationId", "ffffffffffffffffffffffffffffffff")]
    [InlineData("targetRelease", "1.0.2")]
    [InlineData("dataRoot", "/tmp/data")]
    public void InconsistentFenceAndJournalRefuseShutdown(string field, string value)
    {
        JsonObject marker = Record();
        marker[field] = value;
        Assert.Throws<InvalidOperationException>(() => CoreServiceUpdateCommand.ValidateOperation(operation, Record().ToJsonString(), marker.ToJsonString(),
            "/opt/kautuka/manager/versions/1.0.0/kautuka-manager"));
    }

    [Theory]
    [InlineData("phase", "unknown")]
    [InlineData("sourceRelease", "different-source")]
    [InlineData("targetRelease", "../outside")]
    [InlineData("dataRoot", "/tmp/data")]
    public void MatchingButInvalidRecordsAreRefused(string field, string value)
    {
        JsonObject record = Record();
        record[field] = value;
        Assert.Throws<InvalidOperationException>(() => CoreServiceUpdateCommand.ValidateOperation(operation, record.ToJsonString(), record.ToJsonString(),
            "/opt/kautuka/manager/versions/1.0.0/kautuka-manager"));
    }

    [Theory]
    [InlineData("verified", "1.0.1", true)]
    [InlineData("verifying", "1.0.0", false)]
    [InlineData("starting", "1.0.1", true)]
    [InlineData("committed", "1.0.0", false)]
    [InlineData("recovered", "1.0.0", true)]
    [InlineData("completed", "1.0.1", false)]
    [InlineData("aborted", "1.0.0", true)]
    [InlineData("capturing", "1.0.0", false)]
    [InlineData("restoring", "1.0.0", false)]
    public void VerificationSelectsOnlyTheReleaseAllowedByThePhase(string phase, string release, bool allowed)
    {
        JsonObject record = Record();
        record["phase"] = phase;
        string json = record.ToJsonString();
        string executable = "/opt/kautuka/manager/versions/" + release + "/kautuka-manager";
        if (allowed) Assert.Equal(release, CoreServiceUpdateCommand.ValidateOperation(operation, json, json, executable, verification: true));
        else Assert.Throws<InvalidOperationException>(() => CoreServiceUpdateCommand.ValidateOperation(operation, json, json, executable, verification: true));
    }

    private static JsonObject Record() => new()
    {
        ["format"] = 1,
        ["operationId"] = operation,
        ["phase"] = "capturing",
        ["sourceRelease"] = "1.0.0",
        ["targetRelease"] = "1.0.1",
        ["dataRoot"] = "/var/lib/kautuka",
        ["sourcePointers"] = new JsonObject { ["manager"] = "1.0.0" },
    };
}
