namespace Kautuka.Gateway.Infrastructure.AI;

internal interface IInjectedMemoryContextBuilder
{
    string Build(IReadOnlyList<InjectedMemoryFact> facts);
}
