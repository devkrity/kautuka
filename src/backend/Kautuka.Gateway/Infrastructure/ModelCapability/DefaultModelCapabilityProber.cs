using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.ModelCapability;

/// <summary>
/// Runs bounded capability probes against a raw (undecorated) chat client.
/// Each probe has an independent timeout; unknown capabilities remain null.
/// Streaming probe sends a minimal message and checks for incremental updates.
/// Reasoning probe checks whether reasoning content appears in the response.
/// Function calling and structured output probes use declared responses only.
/// </summary>
internal sealed class DefaultModelCapabilityProber(ILogger<DefaultModelCapabilityProber> logger)
    : IModelCapabilityProber
{
    private static readonly TimeSpan probeTimeout = TimeSpan.FromSeconds(20);

    public async ValueTask<ProbedCapabilities> ProbeAsync(
        IChatClient rawClient, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawClient);

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(probeTimeout);

        bool? supportsStreaming = null;
        bool? supportsReasoningChannel = null;
        bool? reasoningMayBeOnlyOutput = null;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Reply with exactly: ok")
        };

        try
        {
            bool gotStreaming = false;
            bool gotReasoning = false;
            bool gotText = false;

            await foreach (ChatResponseUpdate update in
                rawClient.GetStreamingResponseAsync(messages, cancellationToken: probeCts.Token).ConfigureAwait(false))
            {
                foreach (AIContent content in update.Contents)
                {
                    if (content is TextReasoningContent)
                        gotReasoning = true;
                    else if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                        gotText = true;
                }

                if (!gotStreaming) gotStreaming = true;
            }

            supportsStreaming = gotStreaming;
            supportsReasoningChannel = gotReasoning;

            // If we got reasoning tokens but no text, the model may emit reasoning-only output.
            reasoningMayBeOnlyOutput = gotReasoning && !gotText ? true : null;
        }
        catch (OperationCanceledException) when (probeCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            DefaultModelCapabilityProberLog.CapabilityProbeTimedOut(logger);
        }
        catch (HttpRequestException ex)
        {
            DefaultModelCapabilityProberLog.CapabilityProbeFailed(logger, ex);
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            DefaultModelCapabilityProberLog.CapabilityProbeFailed(logger, ex);
        }
        catch (IOException ex)
        {
            DefaultModelCapabilityProberLog.CapabilityProbeFailed(logger, ex);
        }
        catch (InvalidOperationException ex)
        {
            DefaultModelCapabilityProberLog.CapabilityProbeFailed(logger, ex);
        }
        catch (JsonException ex)
        {
            DefaultModelCapabilityProberLog.CapabilityProbeFailed(logger, ex);
        }
        catch (ArgumentOutOfRangeException ex) when (ModelProbeFailureClassifier.IsUnknownFinishReason(ex))
        {
            DefaultModelCapabilityProberLog.CapabilityProbeFailed(logger, ex);
        }

        // ── Tool-call probe ─────────────────────────────────────────────────
        bool? supportsFunctionCalling = null;
        try
        {
            AIFunction probeFunc = AIFunctionFactory.Create(
                (string value) => $"pong:{value}",
                "ping_probe",
                "Returns a pong response for the provided value.",
                serializerOptions: null);

            ChatResponse toolResponse = await rawClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Call ping_probe with value set to ok.")],
                new ChatOptions
                {
                    MaxOutputTokens = 64,
                    Tools = [probeFunc],
                    ToolMode = ChatToolMode.RequireAny,
                    AllowMultipleToolCalls = false,
                },
                probeCts.Token).ConfigureAwait(false);

            supportsFunctionCalling = toolResponse.Messages
                .SelectMany(static m => m.Contents)
                .OfType<FunctionCallContent>()
                .Any(static c => string.Equals(c.Name, "ping_probe", StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (probeCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            DefaultModelCapabilityProberLog.ToolCallProbeTimedOut(logger);
        }
        catch (HttpRequestException ex)
        {
            DefaultModelCapabilityProberLog.ToolCallProbeFailed(logger, ex);
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            DefaultModelCapabilityProberLog.ToolCallProbeFailed(logger, ex);
        }
        catch (IOException ex)
        {
            DefaultModelCapabilityProberLog.ToolCallProbeFailed(logger, ex);
        }
        catch (InvalidOperationException ex)
        {
            DefaultModelCapabilityProberLog.ToolCallProbeFailed(logger, ex);
        }
        catch (JsonException ex)
        {
            DefaultModelCapabilityProberLog.ToolCallProbeFailed(logger, ex);
        }
        catch (ArgumentOutOfRangeException ex) when (ModelProbeFailureClassifier.IsUnknownFinishReason(ex))
        {
            DefaultModelCapabilityProberLog.ToolCallProbeFailed(logger, ex);
            supportsFunctionCalling = false;
        }

        return new ProbedCapabilities
        {
            SupportsStreaming = supportsStreaming,
            SupportsReasoningChannel = supportsReasoningChannel,
            ReasoningMayBeOnlyOutput = reasoningMayBeOnlyOutput,
            SupportsFunctionCalling = supportsFunctionCalling,
            ProbedAt = DateTimeOffset.UtcNow
        };
    }
}

internal static partial class DefaultModelCapabilityProberLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Capability probe timed out. Capabilities will remain unknown.")]
    public static partial void CapabilityProbeTimedOut(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Capability probe failed. Capabilities will remain unknown.")]
    public static partial void CapabilityProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Tool-call capability probe timed out. Capability will remain unknown.")]
    public static partial void ToolCallProbeTimedOut(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Tool-call capability probe failed. Capability will remain unknown.")]
    public static partial void ToolCallProbeFailed(ILogger logger, Exception exception);
}
