using Grpc.Core;
using Grpc.Net.Client;

using Kautuka.Gateway.Infrastructure.Grpc;
using Kautuka.Manager.Protos;
using Kautuka.Telemetry.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class DeferredSuccessTests
{
    [Fact]
    public void FailedInitializationIsAttemptedAgainUntilItSucceeds()
    {
        int calls = 0;
        var deferred = new DeferredSuccess<string>(() =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("not ready");
            }

            return "ready";
        });

        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => _ = deferred.Value);
        Assert.Equal("not ready", missing.Message);
        Assert.Equal("ready", deferred.Value);
        Assert.Equal("ready", deferred.Value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ConcurrentInitializationPublishesOneValue()
    {
        int calls = 0;
        var deferred = new DeferredSuccess<string>(() =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(30);
            return "ready";
        });

        Parallel.For(0, 8, _ => Assert.Equal("ready", deferred.Value));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void MissingManagerCertificateIsLoadedAfterThePeerWritesIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-deferred-cert-" + Guid.NewGuid().ToString("N"));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
            })
            .Build();
        var deferred = new DeferredSuccess<WorkspaceMtlsCerts>(() =>
            new WorkspaceMtlsCerts(configuration, NullLogger<WorkspaceMtlsCerts>.Instance));

        try
        {
            InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => _ = deferred.Value);
            Assert.Contains(".ca.cer", missing.Message, StringComparison.Ordinal);

            var issued = LocalCaMtlsBootstrap.EnsureAndLoad(
                Path.Combine(root, "manager"),
                "Manager",
                [
                    new LocalMtlsClientCertificate(
                        ManagerClientIdentities.GatewayCommonName,
                        ManagerClientIdentities.GatewayCertificateFileName),
                ]);
            issued.ServerCert.Dispose();
            issued.CaCert.Dispose();

            WorkspaceMtlsCerts loaded = deferred.Value;
            Assert.False(string.IsNullOrWhiteSpace(loaded.CaCert.Thumbprint));
            Assert.Same(loaded, deferred.Value);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CallInvokerDoesNotRememberAMissingCertificate()
    {
        int calls = 0;
        GrpcChannel? channel = null;
        var channels = new DeferredSuccess<GrpcChannel>(() =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("Manager CA cert not found");
            }

            channel = GrpcChannel.ForAddress("http://127.0.0.1:9");
            return channel;
        });
        var invoker = new LazyCallInvoker(channels);
        var method = new Method<string, string>(
            MethodType.Unary,
            "kautuka.test",
            "Ping",
            Marshallers.Create(
                static (string value) => System.Text.Encoding.UTF8.GetBytes(value),
                static bytes => System.Text.Encoding.UTF8.GetString(bytes)),
            Marshallers.Create(
                static (string value) => System.Text.Encoding.UTF8.GetBytes(value),
                static bytes => System.Text.Encoding.UTF8.GetString(bytes)));

        try
        {
            InvalidOperationException missing = Assert.Throws<InvalidOperationException>(
                () => invoker.BlockingUnaryCall(method, host: null, new CallOptions(), "ping"));
            Assert.Equal("Manager CA cert not found", missing.Message);

            Exception connected = Assert.ThrowsAny<Exception>(
                () => invoker.BlockingUnaryCall(method, host: null, new CallOptions(), "ping"));
            Assert.IsNotType<InvalidOperationException>(connected);

            _ = Assert.ThrowsAny<Exception>(
                () => invoker.BlockingUnaryCall(method, host: null, new CallOptions(), "ping"));
            Assert.Equal(2, calls);
        }
        finally
        {
            channel?.Dispose();
        }
    }
}
