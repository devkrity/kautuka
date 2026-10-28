using System.Text.Json;

using Kautuka.Gateway.Domain.Channels;

namespace Kautuka.IntegrationTests;

public sealed class ChannelApprovalArgumentsTests
{
    [Fact]
    public void SanitizeRedactsSensitiveNestedFieldsAndPreservesOrdinaryDetails()
    {
        string sanitized = ChannelApprovalArguments.Sanitize(
            "{\"recipient\":\"Example User\",\"emailAddress\":\"amit@example.com\",\"nested\":{\"access_token\":\"secret-value\",\"amount\":42}}")!;
        using JsonDocument document = JsonDocument.Parse(sanitized);
        JsonElement root = document.RootElement;

        Assert.Equal("Example User", root.GetProperty("recipient").GetString());
        Assert.Equal("[redacted]", root.GetProperty("emailAddress").GetString());
        Assert.Equal("[redacted]", root.GetProperty("nested").GetProperty("access_token").GetString());
        Assert.Equal(42, root.GetProperty("nested").GetProperty("amount").GetInt32());
    }

    [Fact]
    public void SanitizeCapsOversizedArgumentsAsValidJson()
    {
        string sanitized = ChannelApprovalArguments.Sanitize(JsonSerializer.Serialize(new { description = new string('x', 5000) }))!;

        Assert.True(sanitized.Length <= 1800);
        using JsonDocument document = JsonDocument.Parse(sanitized);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains("preview", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeOmitsMalformedJsonInsteadOfForwardingIt()
    {
        string sanitized = ChannelApprovalArguments.Sanitize("password=secret-value")!;

        Assert.DoesNotContain("secret-value", sanitized, StringComparison.Ordinal);
        Assert.Contains("omitted", sanitized, StringComparison.Ordinal);
    }
}
