namespace Kautuka.Gateway.Infrastructure.ProviderPathPolicy;

/// <summary>How tool/function-calling is executed on a given provider path.</summary>
internal enum ToolCompatibilityMode
{
    /// <summary>Use the provider's native function-calling API.</summary>
    Native,

    /// <summary>Encode tool intent as a structured JSON prompt (fallback).</summary>
    PromptedJson,
}

/// <summary>How structured JSON responses are requested.</summary>
internal enum JsonCompatibilityMode
{
    /// <summary>Use relaxed JSON guidance (natural language prompt).</summary>
    Relaxed,

    /// <summary>Use strict ResponseFormat.Json where supported.</summary>
    Strict,
}

/// <summary>Whether vision/image inputs are permitted on this path.</summary>
internal enum VisionCompatibilityMode
{
    /// <summary>Vision inputs are not sent; model has not been validated.</summary>
    Disabled,

    /// <summary>Vision inputs are forwarded normally.</summary>
    Enabled,
}

/// <summary>How reasoning-only responses (thinking tokens with no text) are handled.</summary>
internal enum ReasoningCompatibilityMode
{
    /// <summary>Emit text response as-is; no special reasoning handling.</summary>
    TextFirst,

    /// <summary>Promote reasoning tokens to visible text when no text content is produced.</summary>
    PromoteReasoningWhenEmpty,
}
