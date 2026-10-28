namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class DefaultMemoryFactFormatter : IMemoryFactFormatter
{
    public string Format(InjectedMemoryFact fact)
    {
        string category = string.IsNullOrWhiteSpace(fact.Category) ? "general" : fact.Category.Trim();
        string title = string.IsNullOrWhiteSpace(fact.Title) ? "Fact" : fact.Title.Trim();
        string content = fact.Content.Trim();
        return $"- [{category}] {title}: {content}";
    }
}
