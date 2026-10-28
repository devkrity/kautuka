namespace Kautuka.ChannelHost.Hosting;

internal sealed class ChannelAdapterHostedService(
    IEnumerable<IChannelAdapter> adapters) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(adapters.Select(adapter => adapter.RunAsync(stoppingToken)));
}
