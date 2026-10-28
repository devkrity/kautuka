using System.Net;
using System.Text.Json;

using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Hosting.Modules;
using Kautuka.Gateway.Infrastructure.Appliance;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Gateway.Infrastructure.Grpc;
using Kautuka.Gateway.Infrastructure.Updates;
using Kautuka.Gateway.RuntimePlane;
using Kautuka.Telemetry.Security;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Scalar.AspNetCore;

namespace Kautuka.Gateway.Hosting;

internal static partial class GatewayAppBootstrap
{
    private const int workspaceGrpcMaxMessageSizeBytes = 64 * 1024 * 1024;

    [LoggerMessage(EventId = 100, Level = LogLevel.Error,
        Message = "Unhandled exception: {Method} {Path}")]
    private static partial void LogUnhandledException(ILogger logger, Exception? exception, string method, string? path);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning,
        Message = "Rejected unreadable request {Method} {Path} with status {StatusCode}")]
    private static partial void LogRejectedRequest(ILogger logger, string method, string? path, int statusCode);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information,
        Message = "[GatewayStartup] ConfigureApp entered. Ensuring database at {DataDirectory}")]
    private static partial void LogConfigureAppEntered(ILogger logger, string dataDirectory);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "[GatewayStartup] Database ensure ready completed")]
    private static partial void LogDatabaseEnsureReadyCompleted(ILogger logger);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information,
        Message = "[GatewayStartup] Setup token file is {SetupTokenPath}")]
    private static partial void LogSetupTokenIssued(ILogger logger, string setupTokenPath);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "[GatewayStartup] Endpoint mapping completed")]
    private static partial void LogEndpointMappingCompleted(ILogger logger);

    public static void ConfigureProductionServer(WebApplicationBuilder builder, IPAddress publicInterface)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(publicInterface);

        KautukaHostNetworkLayout network = KautukaHostNetworkPolicy.Resolve(builder.Configuration);

        string? configuredAddress = builder.Configuration["Kautuka:Gateway:ListenAddress"];

        // Either an explicit address or a non-loopback interface is a request for a real
        // network listener, and it must keep reaching the TLS check below. Only a purely
        // local Gateway is moved onto a socket.
        bool networkListenerRequested =
            !string.IsNullOrWhiteSpace(configuredAddress) || !IPAddress.IsLoopback(publicInterface);

        if (!networkListenerRequested && network.Gateway.IsUnixSocket)
        {
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(network.Gateway, _ => { }));
            return;
        }

        IPAddress listenAddress = string.IsNullOrWhiteSpace(configuredAddress)
            ? publicInterface
            : IPAddress.Parse(configuredAddress);
        string? certificatePath = builder.Configuration["Kautuka:Gateway:Tls:CertificatePath"];
        string? certificatePassword = builder.Configuration["Kautuka:Gateway:Tls:CertificatePassword"];
        bool remote = !IPAddress.IsLoopback(listenAddress);
        if (remote && string.IsNullOrWhiteSpace(certificatePath))
        {
            throw new InvalidOperationException("Remote Gateway listening requires Kautuka:Gateway:Tls:CertificatePath.");
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(listenAddress, network.GatewayPublicPort, listen =>
            {
                if (!string.IsNullOrWhiteSpace(certificatePath))
                {
                    listen.UseHttps(certificatePath, certificatePassword);
                }
            });
        });
    }

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        KautukaHostNetworkLayout network = KautukaHostNetworkPolicy.Resolve(builder.Configuration);

        // Ensure a KautukaHostLogLevelSwitch is registered so GatewaySettingsStore can be resolved
        // in test harnesses that don't go through Program.cs. Production overrides this with a
        // shared instance via AddSingleton after ConfigureServices returns; last registration wins.
        builder.Services.TryAddSingleton(_ => new KautukaLogLevelSwitch(LogLevel.Warning));
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.AddKautukaTelemetry(
            KautukaActivitySources.ProcessKind.Gateway,
            KautukaActivitySources.Component.Gateway,
            configureMetrics: GatewayObservabilityExtensions.ConfigureGatewayMetrics,
            enableTracing: true);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, KautukaJsonContext.Default);
        });
        builder.Services.AddOpenApi();
        builder.Services.AddKautukaPlatform(builder.Configuration);
        builder.Services.AddKautukaRuntimePlane();

        // Manager mTLS certs (stable CA + rotating client leaf)
        builder.Services.AddSingleton<WorkspaceMtlsCerts>();

        // Manager gRPC channel (mTLS — DO NOT add Http2UnencryptedSupport for this channel)
        // Channel creation waits for the first call and keeps the channel only after the
        // certificate files load. A missing file is attempted again on the next call.
        builder.Services.AddSingleton<IManagerGrpcService>(sp =>
        {
            string baseAddress = network.ManagerGrpcBaseUri.AbsoluteUri;

            var lazyChannel = new DeferredSuccess<GrpcChannel>(() =>
            {
                var mtlsCerts = sp.GetRequiredService<WorkspaceMtlsCerts>();

                SocketsHttpHandler handler = new()
                {
                    ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(network.Manager),
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
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
                    MaxSendMessageSize = workspaceGrpcMaxMessageSizeBytes,
                    MaxReceiveMessageSize = workspaceGrpcMaxMessageSizeBytes,
                    ServiceConfig = new ServiceConfig
                    {
                        MethodConfigs =
                        {
                            new MethodConfig
                            {
                                Names = { MethodName.Default },
                                RetryPolicy = new RetryPolicy
                                {
                                    MaxAttempts = 4,
                                    InitialBackoff = TimeSpan.FromMilliseconds(250),
                                    MaxBackoff = TimeSpan.FromSeconds(2),
                                    BackoffMultiplier = 2,
                                    RetryableStatusCodes = { StatusCode.Unavailable }
                                }
                            }
                        }
                    }
                });
            });

            return new ManagerGrpcServiceClient(
                new DeadlineCallInvoker(new LazyCallInvoker(lazyChannel)),
                baseAddress);
        });
        builder.Services.AddSingleton<IAgentExecutionDependencyProbe, ManagerExecutionDependencyProbe>();
        builder.Services.AddSingleton<IAgentExecutionDependencyProbe, CredentialsHostExecutionDependencyProbe>();
        builder.Services.AddSingleton<IAgentExecutionReadinessService, AgentExecutionReadinessService>();
        builder.Services.AddSingleton<UpdateServiceMtlsCerts>();
        builder.Services.AddSingleton<IUpdateServiceGrpcService>(sp =>
        {
            string updateServiceAddress = network.UpdateServiceGrpcBaseUri.AbsoluteUri;

            var lazyChannel = new DeferredSuccess<GrpcChannel>(() =>
            {
                var mtlsCerts = sp.GetRequiredService<UpdateServiceMtlsCerts>();

                SocketsHttpHandler handler = new()
                {
                    ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(network.UpdateService),
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
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
                            if (cert2.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) != "UpdateService Server") return false;
                            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                            chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(mtlsCerts.CaCert);
                            chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                            return chain.Build(cert2);
                        }
                    }
                };

                return GrpcChannel.ForAddress(updateServiceAddress, new GrpcChannelOptions
                {
                    HttpHandler = handler,
                    ThrowOperationCanceledOnCancellation = true,
                });
            });

            return new UpdateServiceGrpcServiceClient(
                new DeadlineCallInvoker(new LazyCallInvoker(lazyChannel)));
        });
        builder.Services.AddSingleton<UpdateOperationHistoryReader>();
        builder.Services.AddSingleton<SidecarStagingStatusReader>();
        builder.Services.AddSingleton<WorkspaceRuntimeContextResolver>();
        builder.Services.AddSingleton<IContainerMcpConnectorClient, ContainerMcpConnectorClient>();
        builder.Services.AddSingleton<IWorkspaceDesiredStatePublisher, WorkspaceDesiredStatePublisher>();
        builder.Services.AddHostedService<WorkspaceDesiredStateStartupService>();
        // ChannelHost mTLS certs (stable CA + rotating client leaf)
        builder.Services.AddSingleton<ChannelHostMtlsCerts>();

        // ChannelHost gRPC channel (mTLS — DO NOT add Http2UnencryptedSupport for this channel)
        // Channel and certificate loading wait for the first call. A missing file is not
        // remembered, so a later call loads the material after it has been written.
        builder.Services.AddSingleton<IChannelHostGrpcService>(sp =>
        {
            string channelHostAddress = network.ChannelHostGrpcBaseUri.AbsoluteUri;
            var lazyMtlsCerts = new DeferredSuccess<ChannelHostMtlsCerts>(
                () => sp.GetRequiredService<ChannelHostMtlsCerts>());

            var lazyChannel = new DeferredSuccess<GrpcChannel>(() =>
            {
                var mtlsCerts = lazyMtlsCerts.Value;

                SocketsHttpHandler chHandler = new()
                {
                    ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(network.ChannelHost),
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
                            return LocalCaMtlsBootstrap.ValidateExactServerCert(
                                cert2, mtlsCerts.CaCert, "Channel Server", "localhost");
                        }
                    }
                };

                return GrpcChannel.ForAddress(channelHostAddress, new GrpcChannelOptions
                {
                    HttpHandler = chHandler,
                    ThrowOperationCanceledOnCancellation = true,
                });
            });

            return new ChannelHostGrpcServiceClient(
                new DeadlineCallInvoker(new LazyCallInvoker(lazyChannel)),
                channelHostAddress,
                () => lazyMtlsCerts.Value.DelegationSigningCert,
                TimeProvider.System);
        });
        // CredentialsHost mTLS certs (stable CA + rotating client leaf)
        builder.Services.AddSingleton<CredentialsMtlsCerts>();
        builder.Services.AddHealthChecks().AddCheck<CredentialsHostDependencyHealthCheck>("credentialshost-ready", tags: ["ready"]);

        // CredentialsHost gRPC channel (mTLS — DO NOT add Http2UnencryptedSupport for this channel)
        // Callers depend on Lazy<>. PublicationOnly does not remember a failed certificate load,
        // so a missing file is attempted again on the next call.
        builder.Services.AddSingleton(sp => new Lazy<Kautuka.Protos.CredentialsService.CredentialsServiceClient>(() =>
        {
            var mtlsCerts = sp.GetRequiredService<CredentialsMtlsCerts>();
            string baseAddress = network.CredentialsGrpcBaseUri.AbsoluteUri;

            SocketsHttpHandler handler = new()
            {
                ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(network.Credentials),
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => mtlsCerts.ClientCert,
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    {
                        if (cert is null) return false;
                        using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                        if (cert2.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) != "Credentials Server") return false;
                        using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                        chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(mtlsCerts.CaCert);
                        chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                        return chain.Build(cert2);
                    }
                }
            };

            GrpcChannel credChannel = GrpcChannel.ForAddress(baseAddress, new GrpcChannelOptions
            {
                HttpHandler = handler,
                ThrowOperationCanceledOnCancellation = true,
                ServiceConfig = new ServiceConfig
                {
                    MethodConfigs =
                    {
                        new MethodConfig
                        {
                            Names = { MethodName.Default },
                            RetryPolicy = new RetryPolicy
                            {
                                MaxAttempts = 4,
                                InitialBackoff = TimeSpan.FromMilliseconds(250),
                                MaxBackoff = TimeSpan.FromSeconds(2),
                                BackoffMultiplier = 2,
                                RetryableStatusCodes = { StatusCode.Unavailable }
                            }
                        }
                    }
                }
            });

            return new Kautuka.Protos.CredentialsService.CredentialsServiceClient(
                new DeadlineCallInvoker(credChannel.CreateCallInvoker()));
        }, LazyThreadSafetyMode.PublicationOnly));
        builder.AddGatewayModules();
    }

    public static void ConfigureApp(WebApplication app, KautukaDatabase database)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(database);

        LogConfigureAppEntered(app.Logger, database.DataDirectory);

        // No-ops when Gateway is on TCP, which is the case whenever a network listener was
        // requested, because the socket file the mode applies to does not exist.
        app.GrantSocketGroupAccessWhenStarted(
            KautukaHostNetworkPolicy.Resolve(app.Configuration).Gateway);

        database.EnsureReady();
        LogDatabaseEnsureReadyCompleted(app.Logger);
        if (app.Services.GetRequiredService<Domain.Stores.IGatewayUserStore>().CanSetup)
        {
            SetupPossession setupToken = app.Services.GetRequiredService<SetupPossession>();
            setupToken.EnsureIssued();
            LogSetupTokenIssued(app.Logger, setupToken.TokenPath);
        }
        KautukaUsageDatabase usageDatabase = app.Services.GetRequiredService<KautukaUsageDatabase>();
        usageDatabase.TryEnsureReady(app.Logger);

        app.UseExceptionHandler(exApp => exApp.Run(async ctx =>
        {
            var exFeature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            Exception? ex = exFeature?.Error;
            ILogger logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Kautuka.Gateway.UnhandledException");
            int statusCode = StatusCodes.Status500InternalServerError;
            string detail = $"Internal server error. Correlation ID: {ctx.TraceIdentifier}";
            if (ex is Microsoft.AspNetCore.Http.BadHttpRequestException badRequest)
            {
                statusCode = badRequest.StatusCode;
                detail = "The request could not be read.";
                LogRejectedRequest(logger, ctx.Request.Method, ctx.Request.Path.Value, statusCode);
            }
            else
            {
                LogUnhandledException(logger, ex, ctx.Request.Method, ctx.Request.Path.Value);
            }

            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            string payload = JsonSerializer.Serialize(
                new GatewayErrorResponse(detail),
                KautukaJsonContext.Default.GatewayErrorResponse);
            await ctx.Response.WriteAsync(payload, ctx.RequestAborted).ConfigureAwait(false);
        }));

        app.UseKautukaRequestCorrelation();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
        }).AllowAnonymous();
        app.MapOpenApi("/openapi/v1.json");
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference("/scalar");
        }
        app.MapGatewayModules();
        LogEndpointMappingCompleted(app.Logger);
    }
}
