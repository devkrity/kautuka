using Grpc.Net.Client;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Telemetry.Correlation;

namespace Kautuka.UpdateService.Hosting;

public sealed class ManagerStatusClient(UpdateServiceMtlsCerts mtlsCerts, IConfiguration configuration)
    : IManagerStatusClient
{
    private readonly Lazy<GrpcChannel> channel = new(() =>
    {
        KautukaEndpoint manager = KautukaHostNetworkPolicy.Resolve(configuration).Manager;
        string baseAddress = manager.BaseUri.AbsoluteUri;

        SocketsHttpHandler handler = new()
        {
            ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(manager),
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ClientCertificates = [mtlsCerts.ClientCert],
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                    if (cert2.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) != "Manager Server") return false;
                    using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                    chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(mtlsCerts.CaCert);
                    chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                    return chain.Build(cert2);
                }
            }
        };

        return GrpcChannel.ForAddress(baseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
            ThrowOperationCanceledOnCancellation = true,
        });
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<ManagerStatusResponse> GetHostStatusAsync(CancellationToken cancellationToken)
        => new ManagerService.ManagerServiceClient(channel.Value).GetHostStatusAsync(
            new WorkspacePingRequest { Message = "update-host-status" },
            headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(),
            cancellationToken: cancellationToken).ResponseAsync;
}
