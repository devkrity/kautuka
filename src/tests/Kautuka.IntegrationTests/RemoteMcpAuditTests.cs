using Kautuka.Gateway.Domain.Connectors;

using Microsoft.Extensions.Logging;

namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpAuditTests
{
    [Fact]
    public void EmitsOnlyOperationIdentityScopeAndOutcomeMetadata()
    {
        var logger = new Capture();
        RemoteMcpAudit.Record(logger, "credential.replaced", "owner", "search", "user", "succeeded", "api-key");
        Assert.Equal(7340, logger.EventId.Id);
        Assert.Equal("credential.replaced", logger.Fields["Operation"]);
        Assert.Equal("owner", logger.Fields["ActorId"]);
        Assert.Equal("search", logger.Fields["ConnectorId"]);
        Assert.Equal("user", logger.Fields["Scope"]);
        Assert.Equal("succeeded", logger.Fields["Outcome"]);
        Assert.Equal("api-key", logger.Fields["CredentialKey"]);
        Assert.DoesNotContain(logger.Fields.Keys, key => key.Contains("value", StringComparison.OrdinalIgnoreCase));
        Assert.Null(logger.Exception);
    }

    private sealed class Capture : ILogger
    {
        public EventId EventId { get; private set; }
        public Exception? Exception { get; private set; }
        public Dictionary<string, object?> Fields { get; private set; } = new(StringComparer.Ordinal);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            EventId = eventId;
            Exception = exception;
            Fields = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(StringComparer.Ordinal);
        }
    }
}
