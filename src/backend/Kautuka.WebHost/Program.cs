using Kautuka.WebHost;

using Microsoft.Extensions.FileProviders;

KautukaAspNetCoreStartupPolicy.SanitizeHostingStartupAssemblies();

var builder = WebApplication.CreateSlimBuilder(args);
builder.AddKautukaLogging(LogLevel.Debug);
builder.AddKautukaTelemetry(
    KautukaActivitySources.ProcessKind.Web,
    KautukaActivitySources.Component.WebHost);

KautukaHostNetworkLayout network = KautukaHostNetworkPolicy.Resolve(builder.Configuration);

// An explicitly configured Gateway URL is a request for a network hop and wins over
// the socket default, which is how a split deployment or a debugging proxy is pointed
// somewhere other than the local Gateway.
string? configuredGatewayUrl = builder.Configuration["WebHost:GatewayUrl"];
string gatewayBaseUrl = configuredGatewayUrl ?? network.GatewayPublicBaseUri.AbsoluteUri;
bool gatewayOverSocket = string.IsNullOrWhiteSpace(configuredGatewayUrl) && network.Gateway.IsUnixSocket;

string configuredWebRoot = builder.Configuration["WebHost:ContentRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");

string webRoot = ResolveDirectoryPath(configuredWebRoot, Path.Combine(AppContext.BaseDirectory, "wwwroot"));

int publicPort = network.WebHostPort;

using WebHostEdgeBinding edgeBinding = WebHostEdgePolicy.Resolve(
    builder.Configuration,
    publicPort);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(edgeBinding.Address, edgeBinding.Port, listenOptions =>
    {
        if (edgeBinding.Certificate is not null)
            listenOptions.UseHttps(edgeBinding.Certificate);
    });
});

IHttpClientBuilder gatewayClient = builder.Services.AddHttpClient("gateway", client =>
{
    client.BaseAddress = new Uri(gatewayBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(300);
});

// The handler is shared by every browser. The default cookie jar would keep a
// refresh cookie and attach it to later requests, then a second refresh revokes
// the browser's session.
gatewayClient.ConfigurePrimaryHttpMessageHandler(() =>
{
    SocketsHttpHandler handler = new()
    {
        UseCookies = false,
    };
    if (gatewayOverSocket)
    {
        handler.ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(network.Gateway);
    }

    return handler;
});

using WebApplication app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(static state =>
    {
        WebHostSecurityHeaders.Apply(((HttpContext)state!).Response.Headers);
        return Task.CompletedTask;
    }, context);
    await next(context).ConfigureAwait(false);
});

if (Directory.Exists(webRoot))
{
    var fileProvider = new PhysicalFileProvider(webRoot);

    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fileProvider,
        RequestPath = string.Empty
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        RequestPath = string.Empty
    });

    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fileProvider });
}
else
{
    app.MapGet("/", () => Results.Text(
    $"Kautuka.WebHost static content root not found: {configuredWebRoot} (resolved as {webRoot})",
        "text/plain"));
}

app.Map("/api/{**path}", async (string path, HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    if (GatewayProxy.TargetsInternalRoute(context.Request.Path.Value ?? string.Empty))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    HttpClient httpClient = httpClientFactory.CreateClient("gateway");
    await GatewayProxy.ForwardAsync(context, httpClient, app.Logger).ConfigureAwait(false);
});

await app.RunAsync().ConfigureAwait(false);

static string ResolveDirectoryPath(string configuredPath, string fallbackPath)
{
    if (Path.IsPathRooted(configuredPath))
    {
        return Directory.Exists(configuredPath)
            ? configuredPath
            : Path.GetFullPath(fallbackPath);
    }

    foreach (string basePath in EnumerateBasePaths())
    {
        string candidate = Path.GetFullPath(Path.Combine(basePath, configuredPath));
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    return Path.GetFullPath(fallbackPath);
}

static IEnumerable<string> EnumerateBasePaths()
{
    foreach (string root in new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory,
    })
    {
        foreach (string candidate in EnumerateAncestors(root))
        {
            yield return candidate;
        }
    }
}

static IEnumerable<string> EnumerateAncestors(string startPath)
{
    string? current = Path.GetFullPath(startPath);
    while (!string.IsNullOrEmpty(current))
    {
        yield return current;
        current = Directory.GetParent(current)?.FullName;
    }
}
