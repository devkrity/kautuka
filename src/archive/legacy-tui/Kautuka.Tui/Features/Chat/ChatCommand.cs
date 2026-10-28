using System.Text;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Chat;

public sealed class ChatCommand
{
    public static IRenderable BuildSessionsRenderable(string agentId, IReadOnlyList<GatewaySession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        if (sessions.Count == 0)
        {
            return new Panel("[grey]No sessions found for that agent.[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader($"Sessions | {Markup.Escape(agentId)}", Justify.Left),
            };
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Session Id");
        table.AddColumn("Updated");
        table.AddColumn("Messages");
        table.AddColumn("Compacted");
        table.AddColumn("Title");

        foreach (GatewaySession session in sessions.OrderByDescending(session => session.UpdatedAt, StringComparer.Ordinal))
        {
            table.AddStripedRow(
                session.Id,
                session.UpdatedAt,
                session.Messages.Count.ToString(),
                session.Compacted ? "yes" : "no",
                string.IsNullOrWhiteSpace(session.Title) ? "-" : session.Title);
        }

        return new Rows(
            new Panel($"[grey]Agent:[/] {Markup.Escape(agentId)}\n[grey]Sessions:[/] {sessions.Count}")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Chat Sessions", Justify.Left),
            },
            table);
    }

    public static IRenderable BuildSessionRenderable(GatewaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Table summary = StripedTable.CreateRounded();
        summary.AddColumn("Field");
        summary.AddColumn("Value");
        summary.AddStripedRow("Session", session.Id);
        summary.AddStripedRow("Target", $"{session.TargetType}:{session.TargetId}");
        summary.AddStripedRow("Updated", session.UpdatedAt);
        summary.AddStripedRow("Messages", session.Messages.Count.ToString());
        summary.AddStripedRow("Compacted", session.Compacted ? "yes" : "no");
        summary.AddStripedRow("Summary", session.CompactSummary ?? "-");

        return new Rows(summary, BuildTranscriptRenderable(session.Messages));
    }

    public static async Task<Result> ExecuteAsync(TuiCommandOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 2 && args[0].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return await StartChatAsync(options, args[1], null, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 3 && args[0].Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            return await StartChatAsync(options, args[1], args[2], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("sessions", StringComparison.OrdinalIgnoreCase))
        {
            return await ListSessionsAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("show-session", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowSessionAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: chat start <agentId> | chat resume <agentId> <sessionId> | chat sessions <agentId> | chat show-session <sessionId>");
    }

    private static async Task<Result> StartChatAsync(TuiCommandOptions options, string agentId, string? sessionId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;

        GatewaySession? session;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Result<GatewaySession> createSessionResult = await client.CreateSessionAsync("agent", agentId, cancellationToken).ConfigureAwait(false);
            if (!createSessionResult.IsSuccess || createSessionResult.Value is null)
            {
                return Results.Failure(createSessionResult.ErrorMessage ?? "Failed to create chat session.");
            }

            session = createSessionResult.Value;
        }
        else
        {
            Result<GatewaySession> sessionResult = await client.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!sessionResult.IsSuccess || sessionResult.Value is null)
            {
                return Results.Failure(sessionResult.ErrorMessage ?? "Failed to load session.");
            }

            session = sessionResult.Value;
            if (!string.Equals(session.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(session.TargetId, agentId, StringComparison.Ordinal))
            {
                return Results.Failure("The selected session does not belong to that agent.");
            }
        }

        RenderChatHeader(agentId, session.Id);
        if (session.Messages.Count > 0)
        {
            RenderTranscript(session.Messages);
        }

        while (true)
        {
            RenderPromptSeparator();
            Console.Write("❯ ");
            string prompt = Console.ReadLine() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return Results.Success();
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold deepskyblue1]❯[/] {Markup.Escape(prompt.Trim())}");

            ChatRenderState renderState = new();
            TaskCompletionSource firstVisibleResponseEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            WaitingSpinner? spinner = null;
            bool interruptRequested = false;

            Result<GatewayNaradCapabilitiesResponse> capabilitiesResult =
                await client.GetNaradCapabilitiesAsync(agentId, session.Id, cancellationToken).ConfigureAwait(false);
            long expectedRevision = capabilitiesResult.IsSuccess && capabilitiesResult.Value is not null
                ? capabilitiesResult.Value.CurrentSessionSequence
                : 0;

            try
            {
                spinner = StartWaitingSpinner();

#pragma warning disable CA2000
                CancellationTokenSource streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
#pragma warning restore CA2000
                try
                {
                    Task<Result> eventStreamTask = client.RunAgentChatAsync(
                        agentId,
                        session.Id,
                        prompt.Trim(),
                        expectedRevision,
                        (evt, streamToken) =>
                        {
                            switch (evt.Type)
                            {
                                case "run.started":
                                    return ValueTask.CompletedTask;
                                case "reasoning.started":
                                case "reasoning.delta":
                                    firstVisibleResponseEvent.TrySetResult();
                                    renderState.EnsureThinkingSection();
                                    string? reasoningDelta = SanitizeStreamDelta(evt.Delta);
                                    if (!string.IsNullOrEmpty(reasoningDelta))
                                    {
                                        renderState.AppendThinking(reasoningDelta);
                                    }
                                    return ValueTask.CompletedTask;
                                case "reasoning.completed":
                                    renderState.CloseThinkingSection();
                                    return ValueTask.CompletedTask;
                                case "message.text.started":
                                    renderState.EnsureAssistantSection();
                                    return ValueTask.CompletedTask;
                                case "message.text.delta":
                                    firstVisibleResponseEvent.TrySetResult();
                                    renderState.EnsureAssistantSection();
                                    string? assistantDelta = SanitizeStreamDelta(evt.Delta);
                                    if (!string.IsNullOrEmpty(assistantDelta))
                                    {
                                        renderState.AppendAssistant(assistantDelta);
                                    }
                                    return ValueTask.CompletedTask;
                                case "message.text.completed":
                                    renderState.CloseAssistantSection();
                                    return ValueTask.CompletedTask;
                                case "tool.call.started":
                                    firstVisibleResponseEvent.TrySetResult();
                                    renderState.ShowToolCall(evt.ToolName ?? evt.ToolCallId ?? "unknown");
                                    return ValueTask.CompletedTask;
                                case "tool.call.finished":
                                    firstVisibleResponseEvent.TrySetResult();
                                    if (!string.IsNullOrWhiteSpace(evt.Content))
                                    {
                                        renderState.ShowToolResult(evt.Content);
                                    }
                                    else if (!string.IsNullOrWhiteSpace(evt.Outcome))
                                    {
                                        renderState.ShowToolResult(evt.Outcome);
                                    }
                                    return ValueTask.CompletedTask;
                                case "interrupt.requested":
                                    interruptRequested = true;
                                    firstVisibleResponseEvent.TrySetResult();
                                    renderState.ApprovalPending = true;
                                    renderState.ShowApprovalNotice();
                                    AbortAgentChatStream(streamCancellation);
                                    return ValueTask.CompletedTask;
                                case "progress.recorded":
                                    string? progressContent = evt.Message ?? evt.Content ?? evt.Title ?? evt.Operation;
                                    if (!string.IsNullOrWhiteSpace(progressContent))
                                    {
                                        firstVisibleResponseEvent.TrySetResult();
                                        renderState.ShowInternalEvent(
                                            evt.Status ?? "progress",
                                            progressContent);
                                    }
                                    return ValueTask.CompletedTask;
                                case "run.error":
                                    firstVisibleResponseEvent.TrySetResult();
                                    renderState.RunError = evt.Message ?? evt.Content ?? "The agent run failed.";
                                    renderState.ShowInternalEvent("run-error", renderState.RunError);
                                    return ValueTask.CompletedTask;
                                case "run.finished":
                                case "run.cancelled":
                                    firstVisibleResponseEvent.TrySetResult();
                                    return ValueTask.CompletedTask;
                            }

                            return ValueTask.CompletedTask;
                        },
                        streamCancellation.Token);

                    Task completedTask = await Task.WhenAny(firstVisibleResponseEvent.Task, eventStreamTask).ConfigureAwait(false);
                    await completedTask.ConfigureAwait(false);

                    await spinner.StopAsync().ConfigureAwait(false);

                    if (!interruptRequested)
                    {
                        Result eventStreamResult = await eventStreamTask.ConfigureAwait(false);

                        renderState.FinishOpenSections();

                        AnsiConsole.WriteLine();

                        if (!eventStreamResult.IsSuccess)
                        {
                            return Results.Failure(eventStreamResult.ErrorMessage ?? "Agent chat failed.");
                        }

                        if (!string.IsNullOrWhiteSpace(renderState.RunError))
                        {
                            return Results.Failure(renderState.RunError);
                        }

                        if (!renderState.AssistantTextStarted && !renderState.ApprovalPending)
                        {
                            AnsiConsole.MarkupLine("[yellow]Run completed without assistant text.[/]");
                        }
                    }
                    else
                    {
                        renderState.FinishOpenSections();
                        AnsiConsole.WriteLine();
                        _ = await eventStreamTask.ConfigureAwait(false);
                    }

                    Result<GatewaySession> refreshedSessionResult = await client.GetSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                    if (refreshedSessionResult.IsSuccess && refreshedSessionResult.Value is not null)
                    {
                        session = refreshedSessionResult.Value;
                    }
                }
                finally
                {
                    streamCancellation.Dispose();
                }
            }
            finally
            {
                if (spinner is not null)
                {
                    await spinner.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static void AbortAgentChatStream(CancellationTokenSource streamCancellation)
    {
#pragma warning disable CA1849
        streamCancellation.Cancel();
#pragma warning restore CA1849
    }

    private static async Task RunWaitingSpinnerAsync(CancellationToken cancellationToken)
    {
        string[] frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        int frameIndex = 0;
        int renderedLength = 0;

        try
        {
            renderedLength = WriteSpinnerFrame($"● {frames[0]} Thinking...", renderedLength);

            while (!cancellationToken.IsCancellationRequested)
            {
                string frame = frames[frameIndex % frames.Length];
                string line = $"● {frame} Thinking...";
                renderedLength = WriteSpinnerFrame(line, renderedLength);
                frameIndex++;

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ClearSpinnerFrame(renderedLength);
        }
    }

    private static int WriteSpinnerFrame(string text, int previousLength)
    {
        StringBuilder builder = new();
        builder.Append('\r');
        builder.Append(text);

        int padding = Math.Max(0, previousLength - text.Length);
        if (padding > 0)
        {
            builder.Append(' ', padding);
        }

        Console.Write(builder.ToString());
        return text.Length;
    }

    private static void ClearSpinnerFrame(int renderedLength)
    {
        if (renderedLength <= 0)
        {
            return;
        }

        Console.Write('\r');
        Console.Write(new string(' ', renderedLength));
        Console.Write('\r');
    }

    private sealed class WaitingSpinner : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task spinnerTask;
        private bool stopped;

        public WaitingSpinner()
        {
            spinnerTask = RunWaitingSpinnerAsync(cancellation.Token);
        }

        public async Task StopAsync()
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            await cancellation.CancelAsync().ConfigureAwait(false);
            await spinnerTask.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    private static WaitingSpinner StartWaitingSpinner()
        => new();

    private static void RenderChatHeader(string agentId, string sessionId)
    {
        Panel panel = new($"[bold]Agent Chat[/]\n[grey]Agent:[/] {Markup.Escape(agentId)}\n[grey]Session:[/] {Markup.Escape(sessionId)}\n[grey]Tip:[/] Blank prompt exits chat.")
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0),
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static void RenderPromptSeparator()
    {
        AnsiConsole.MarkupLine("[grey]────────────────────────────────────────────────────────────────[/]");
    }

    private static string? SanitizeStreamDelta(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string sanitized = value
            .Replace("<think>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</think>", string.Empty, StringComparison.OrdinalIgnoreCase);

        return sanitized;
    }

    private sealed class ChatRenderState
    {
        private readonly StringBuilder thinkingBuffer = new();
        private bool thinkingActive;
        private bool assistantHeaderPrinted;
        private bool assistantLineOpen;

        public bool AssistantTextStarted { get; set; }

        public bool ApprovalPending { get; set; }

        public string? RunError { get; set; }

        public void EnsureThinkingSection()
        {
            if (!thinkingActive)
            {
                CloseAssistantSection();
                thinkingActive = true;
            }
        }

        public void AppendThinking(string delta)
        {
            EnsureThinkingSection();
            thinkingBuffer.Append(delta);
        }

        public void CloseThinkingSection()
        {
            if (!thinkingActive)
            {
                return;
            }

            thinkingActive = false;

            string content = thinkingBuffer.ToString().Trim();
            thinkingBuffer.Clear();

            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            AnsiConsole.Write(CreateEventPanel(
                "Thinking",
                $"[grey]{Markup.Escape(content)}[/]",
                "grey"));
        }

        public void EnsureAssistantSection()
        {
            if (!assistantHeaderPrinted)
            {
                CloseThinkingSection();
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[mediumspringgreen]● Assistant[/]");
                AnsiConsole.Write("  ");
                assistantHeaderPrinted = true;
                assistantLineOpen = true;
                AssistantTextStarted = true;
            }
        }

        public void AppendAssistant(string delta)
        {
            EnsureAssistantSection();
            AnsiConsole.Write(new Text(delta));
        }

        public void CloseAssistantSection()
        {
            if (!assistantLineOpen)
            {
                return;
            }

            AnsiConsole.WriteLine();
            assistantLineOpen = false;
            assistantHeaderPrinted = false;
        }

        public void ShowToolCall(string toolName)
        {
            FinishOpenSections();
            AnsiConsole.Write(CreateEventPanel(
                "Tool Call",
                $"[bold]name:[/] {Markup.Escape(toolName)}",
                "deepskyblue1"));
        }

        public void ShowToolArgs(string argumentsJson)
        {
            FinishOpenSections();
            AnsiConsole.Write(CreateEventPanel(
                "Tool Arguments",
                $"[grey]{Markup.Escape(TrimForDisplay(argumentsJson, 320))}[/]",
                "deepskyblue1"));
        }

        public void ShowToolResult(string content)
        {
            FinishOpenSections();
            AnsiConsole.Write(CreateEventPanel(
                "Tool Result",
                $"[grey]{Markup.Escape(TrimForDisplay(content, 320))}[/]",
                "deepskyblue1"));
        }

        public void ShowApprovalNotice()
        {
            FinishOpenSections();
            AnsiConsole.Write(CreateEventPanel(
                "Approval Required",
                "[grey]Approval response is not wired in the TUI yet.[/]",
                "yellow"));
        }

        public void ShowInternalEvent(string eventName, string? content)
        {
            FinishOpenSections();
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            string body = $"[grey]{Markup.Escape(TrimForDisplay(content, 320))}[/]";
            AnsiConsole.Write(CreateEventPanel(
                $"Internal: {eventName}",
                body,
                "grey"));
        }

        public void FinishOpenSections()
        {
            CloseThinkingSection();
            CloseAssistantSection();
        }

        private static string TrimForDisplay(string value, int maxLength)
            => value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");

        private static Panel CreateEventPanel(string title, string body, string borderColor)
        {
            Panel panel = new(body)
            {
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse(borderColor),
                Header = new PanelHeader(Markup.Escape(title), Justify.Left),
                Padding = new Padding(1, 0, 1, 0),
            };

            return panel;
        }
    }

    private static async Task<Result> ListSessionsAsync(TuiCommandOptions options, string agentId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewaySessionsResponse> sessionsResult = await client.GetSessionsForAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!sessionsResult.IsSuccess || sessionsResult.Value is null)
        {
            return Results.Failure(sessionsResult.ErrorMessage ?? "Failed to load sessions.");
        }

        AnsiConsole.Write(BuildSessionsRenderable(agentId, sessionsResult.Value.Sessions));
        return Results.Success();
    }

    private static async Task<Result> ShowSessionAsync(TuiCommandOptions options, string sessionId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewaySession> sessionResult = await client.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!sessionResult.IsSuccess || sessionResult.Value is null)
        {
            return Results.Failure(sessionResult.ErrorMessage ?? "Failed to load session.");
        }

        GatewaySession session = sessionResult.Value;
        AnsiConsole.Write(BuildSessionRenderable(session));
        return Results.Success();
    }

    public static IRenderable BuildTranscriptRenderable(IReadOnlyList<GatewayMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return new Markup("[grey]No messages yet.[/]");
        }

        List<IRenderable> rows = [];
        foreach (GatewayMessage message in messages)
        {
            rows.Add(BuildMessageRenderable(message));
        }

        return new Rows([.. rows]);
    }

    private static void RenderTranscript(IReadOnlyList<GatewayMessage> messages)
    {
        AnsiConsole.Write(BuildTranscriptRenderable(messages));
    }

    private static Panel BuildMessageRenderable(GatewayMessage message)
    {
        string color = message.Role switch
        {
            "assistant" => "mediumspringgreen",
            "user" => "deepskyblue1",
            "system" => "yellow",
            _ => "grey",
        };

        List<IRenderable> rows = [];
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            rows.Add(new Markup(Markup.Escape(message.Content)));
        }

        if (!string.IsNullOrWhiteSpace(message.Thinking))
        {
            rows.Add(new Markup($"[grey]thinking:[/] {Markup.Escape(message.Thinking)}"));
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (GatewayToolCall toolCall in message.ToolCalls)
            {
                rows.Add(new Markup($"[grey]tool[/] {Markup.Escape(toolCall.ToolName)}  [grey]status:[/] {Markup.Escape(toolCall.Status)}"));
            }
        }

        if (message.AgentSteps is { Count: > 0 })
        {
            foreach (GatewayAgentStep step in message.AgentSteps)
            {
                rows.Add(new Markup($"[grey]step[/] {Markup.Escape(step.Agent)}: {Markup.Escape(step.Response)}"));
            }
        }

        if (message.Assets is { Count: > 0 })
        {
            rows.Add(new Markup($"[grey]assets:[/] {Markup.Escape(string.Join(", ", message.Assets.Select(static asset => asset.FileName)))}"));
        }

        if (rows.Count == 0)
        {
            rows.Add(new Markup("[grey]No visible content.[/]"));
        }

        return new Panel(new Rows([.. rows]))
        {
            Header = new PanelHeader($"[{color}]{Markup.Escape(message.Role)}[/] [grey]{Markup.Escape(message.Timestamp)}[/]", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse(color),
            Expand = true,
        };
    }
}
