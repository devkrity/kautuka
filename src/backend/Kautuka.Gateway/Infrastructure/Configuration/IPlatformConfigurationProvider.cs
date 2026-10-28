namespace Kautuka.Gateway.Infrastructure.Configuration;

internal interface IPlatformConfigurationProvider
{
    PlatformConfiguration GetCurrent();
}
