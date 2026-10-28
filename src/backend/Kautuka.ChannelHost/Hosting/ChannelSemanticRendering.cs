using System.Text;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Hosting;

internal static class ChannelSemanticRendering
{
    public static JsonObject RenderSlack(ChannelSemanticMessageAction action)
    {
        Validate(action, ChannelProviderCapabilityCatalog.Slack);
        JsonArray blocks = [];
        if (!string.IsNullOrWhiteSpace(action.Document.SenderDisplayName))
        {
            blocks.Add((JsonNode?)SlackTextSection($"*{EscapeSlack(action.Document.SenderDisplayName)}*"));
        }

        AddSlackBlocks(blocks, action.Document.Blocks, action.Document.Template);

        JsonObject body = new()
        {
            ["channel"] = action.ConversationId,
            ["text"] = string.IsNullOrWhiteSpace(action.Document.SenderDisplayName)
                ? GetFallbackText(action.Document)
                : $"{action.Document.SenderDisplayName}\n{GetFallbackText(action.Document)}",
            ["blocks"] = blocks,
        };
        if (!string.IsNullOrWhiteSpace(action.ConversationThreadId))
        {
            body["thread_ts"] = action.ConversationThreadId;
        }

        return body;
    }

    private static void AddSlackBlocks(JsonArray output, IReadOnlyList<ChannelSemanticBlock> source, string template, bool requestDetails = false, bool insideDetails = false)
    {
        foreach (ChannelSemanticBlock block in source)
        {
            switch (block.Type)
            {
                case ChannelSemanticBlockTypes.Header:
                case ChannelSemanticBlockTypes.Heading:
                    output.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "header",
                        ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = block.Text ?? string.Empty, ["emoji"] = true },
                    });
                    break;
                case ChannelSemanticBlockTypes.Section:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        string text = requestDetails
                            ? $"```\n{block.Text}\n```"
                            : block.Text;
                        output.Add((JsonNode?)SlackTextSection(SlackMrkdwn(text, template == "agent_response" && !requestDetails && !insideDetails)));
                    }
                    if (block.Actions?.Count > 0) output.Add((JsonNode?)RenderSlackActions(block.Actions));
                    break;
                case ChannelSemanticBlockTypes.Context:
                    output.Add((JsonNode?)SlackContextBlock(EscapeSlack(block.Text)));
                    if (block.Actions?.Count > 0) output.Add((JsonNode?)RenderSlackActions(block.Actions));
                    break;
                case ChannelSemanticBlockTypes.Details:
                    // Slack Block Kit has no collapsible message block. Keep the
                    // detail label and content visible and formatted as a code block.
                    output.Add((JsonNode?)SlackTextSection($"*{EscapeSlack(block.Text)}*"));
                    AddSlackBlocks(output, block.Blocks ?? [], template,
                        requestDetails || string.Equals(block.Text, "Request details", StringComparison.Ordinal), insideDetails: true);
                    break;
                case ChannelSemanticBlockTypes.Fields:
                    output.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "section",
                        ["fields"] = new JsonArray([.. (block.Fields ?? []).Select(field => (JsonNode?)new JsonObject
                        {
                            ["type"] = "mrkdwn",
                            ["text"] = $"*{EscapeSlack(field.Label)}*\n{EscapeSlack(field.Value)}",
                        })]),
                    });
                    break;
                case ChannelSemanticBlockTypes.Divider:
                    output.Add((JsonNode?)new JsonObject { ["type"] = "divider" });
                    break;
                case ChannelSemanticBlockTypes.Image:
                    output.Add((JsonNode?)new JsonObject { ["type"] = "image", ["image_url"] = block.ImageUrl?.AbsoluteUri, ["alt_text"] = block.AltText ?? "Image" });
                    break;
                case ChannelSemanticBlockTypes.Table:
                    output.Add((JsonNode?)SlackTextSection(SlackMrkdwn(RenderMarkdownTable(block), false)));
                    break;
                case ChannelSemanticBlockTypes.Actions:
                    output.Add((JsonNode?)RenderSlackActions(block.Actions ?? []));
                    break;
            }
        }
    }

    public static JsonArray CreateSlackTextBlocks(string text, string? senderDisplayName)
    {
        JsonArray blocks = [];
        if (text.Length > 3_000)
        {
            return blocks;
        }

        blocks.Add((JsonNode?)SlackTextSection(EscapeSlack(
            string.IsNullOrWhiteSpace(senderDisplayName) ? text : $"*{EscapeSlack(senderDisplayName)}*\n{text}")));
        return blocks;
    }

    public static JsonObject RenderTelegram(ChannelSemanticMessageAction action)
    {
        Validate(action, ChannelProviderCapabilityCatalog.Telegram);
        StringBuilder markdown = new();
        foreach (ChannelSemanticBlock block in action.Document.Blocks)
        {
            switch (block.Type)
            {
                case ChannelSemanticBlockTypes.Header:
                case ChannelSemanticBlockTypes.Heading:
                    AppendLine(markdown, $"## {EscapeTelegramMarkdown(block.Text)}");
                    break;
                case ChannelSemanticBlockTypes.Section:
                    {
                        // Preserve authored Markdown only for the model response. All
                        // other semantic text is data; generated controls are appended
                        // separately as Telegram's native rich-message tags.
                        string sectionText = EscapeTelegramMarkdown(
                            action.Document.Template == "user_message" ? "Prompt: " + block.Text : block.Text,
                            preserveMarkdown: action.Document.Template == "agent_response");
                        AppendLine(markdown, block.Actions?.Count > 0
                            ? $"<p>{sectionText} {RenderTelegramRichActions(block.Actions, inline: true)}</p>\n"
                            : sectionText);
                        break;
                    }
                case ChannelSemanticBlockTypes.Context:
                    AppendLine(markdown, $"_{EscapeTelegramMarkdown(block.Text)}_");
                    if (block.Text?.StartsWith("Session: ", StringComparison.Ordinal) == true) markdown.AppendLine();
                    break;
                case ChannelSemanticBlockTypes.Fields:
                    foreach (ChannelSemanticField field in block.Fields ?? [])
                    {
                        AppendLine(markdown, $"**{EscapeTelegramMarkdown(field.Label)}**\n{EscapeTelegramMarkdown(field.Value)}");
                    }
                    break;
                case ChannelSemanticBlockTypes.Divider:
                    AppendLine(markdown, "---");
                    break;
                case ChannelSemanticBlockTypes.Image:
                    {
                        string imageLabel = EscapeTelegramMarkdown(block.AltText ?? "Image");
                        string? imageUrl = block.ImageUrl?.AbsoluteUri;
                        AppendLine(markdown, string.IsNullOrWhiteSpace(imageUrl)
                            ? imageLabel
                            : $"[{imageLabel}]({imageUrl.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)})");
                        break;
                    }
                case ChannelSemanticBlockTypes.Table:
                    AppendLine(markdown, RenderTelegramMarkdownTable(block));
                    break;
                case ChannelSemanticBlockTypes.Actions:
                    AppendLine(markdown, "\n" + RenderTelegramRichActions(block.Actions ?? []) + "\n");
                    break;
                case ChannelSemanticBlockTypes.Details:
                    AppendLine(markdown, RenderTelegramDetails(block));
                    break;
            }
        }

        string renderedMarkdown = markdown.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(action.Document.SenderDisplayName))
        {
            renderedMarkdown = $"**{EscapeTelegramMarkdown(action.Document.SenderDisplayName)}**\n\n{renderedMarkdown}";
        }

        JsonObject richMessage = new()
        {
            ["markdown"] = renderedMarkdown,
            // Prevent plain text such as URLs and @mentions from silently
            // becoming clickable entities in approval or status messages.
            ["skip_entity_detection"] = true,
        };
        JsonObject body = new()
        {
            ["chat_id"] = action.ConversationId,
            ["rich_message"] = richMessage,
        };
        // Telegram only permits editing messages that have no reply markup or have
        // inline keyboards. ReplyKeyboardMarkup is chat-scoped/persistent, but adding
        // it to an approval/action card makes that specific message ineligible for
        // editMessageText. Set the persistent keyboard on ordinary messages; action
        // cards inherit it from the chat and remain editable after they are resolved.
        if (!ContainsActions(action.Document.Blocks))
        {
            body["reply_markup"] = CreateTelegramReplyKeyboardMarkup();
        }

        if (long.TryParse(action.ReplyToMessageId, out long promptMessageId))
            body["reply_parameters"] = new JsonObject { ["message_id"] = promptMessageId, ["allow_sending_without_reply"] = true };
        return body;
    }

    private static bool ContainsActions(IReadOnlyList<ChannelSemanticBlock> blocks)
        => blocks.Any(block => (block.Actions?.Count ?? 0) > 0 || ContainsActions(block.Blocks ?? []));

    internal static JsonObject CreateTelegramReplyKeyboardMarkup()
        => new()
        {
            ["keyboard"] = new JsonArray(
                new JsonArray(JsonValue.Create("👥 Agents"), JsonValue.Create("🗂 Sessions")),
                new JsonArray(JsonValue.Create("➕ New session"), JsonValue.Create("ℹ️ Current context")),
                new JsonArray(JsonValue.Create("📥 Queue"))),
            ["is_persistent"] = true,
            ["resize_keyboard"] = true,
            ["input_field_placeholder"] = "Choose an action or write a prompt",
        };

    private static string RenderTelegramDetails(ChannelSemanticBlock block)
    {
        var html = new StringBuilder(block.Expanded ? "<details open>" : "<details>");
        html.Append("<summary>").Append(System.Net.WebUtility.HtmlEncode(block.Text ?? string.Empty)).AppendLine("</summary>");
        foreach (ChannelSemanticBlock child in block.Blocks ?? [])
        {
            string text = (System.Net.WebUtility.HtmlEncode(child.Text ?? string.Empty) ?? string.Empty)
                .Replace("\n", "<br>", StringComparison.Ordinal);
            switch (child.Type)
            {
                case ChannelSemanticBlockTypes.Details:
                    html.AppendLine(RenderTelegramDetails(child));
                    break;
                case ChannelSemanticBlockTypes.Header:
                case ChannelSemanticBlockTypes.Heading:
                    html.Append("<p><b>").Append(text).AppendLine("</b></p>");
                    break;
                case ChannelSemanticBlockTypes.Section:
                case ChannelSemanticBlockTypes.Context:
                    if (string.Equals(block.Text, "Request details", StringComparison.Ordinal)
                        && child.Type == ChannelSemanticBlockTypes.Section)
                    {
                        // Telegram parses Markdown within <details>; a <pre> keeps
                        // argument JSON literal and readable without Markdown escapes.
                        html.Append("<pre>").Append(text).AppendLine("</pre>");
                    }
                    else
                    {
                        html.Append("<p>").Append(text).Append(' ').Append(RenderTelegramRichActions(child.Actions ?? [], inline: true)).AppendLine("</p>");
                    }
                    break;
                case ChannelSemanticBlockTypes.Actions:
                    html.AppendLine(RenderTelegramRichActions(child.Actions ?? []));
                    break;
                default: throw new InvalidOperationException("unsupported_details_block:" + child.Type);
            }
        }
        return html.AppendLine("</details>").ToString();
    }

    private static IEnumerable<ChannelSemanticBlock> FlattenBlocks(IReadOnlyList<ChannelSemanticBlock> blocks, int depth = 0)
    {
        if (depth > 3) throw new InvalidOperationException("semantic_details_depth_exceeded");
        foreach (ChannelSemanticBlock block in blocks)
        {
            yield return block;
            if (block.Blocks is not { Count: > 0 }) continue;
            if (block.Type != ChannelSemanticBlockTypes.Details) throw new InvalidOperationException("semantic_children_require_details");
            foreach (ChannelSemanticBlock child in FlattenBlocks(block.Blocks, depth + 1)) yield return child;
        }
    }

    public static string GetFallbackText(ChannelSemanticMessageDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.FallbackText))
        {
            return document.FallbackText;
        }

        StringBuilder text = new();
        foreach (ChannelSemanticBlock block in FlattenBlocks(document.Blocks))
        {
            if (!string.IsNullOrWhiteSpace(block.Text)) AppendLine(text, block.Text);
            foreach (ChannelSemanticField field in block.Fields ?? []) AppendLine(text, $"{field.Label}: {field.Value}");
            foreach (ChannelSemanticAction action in block.Actions ?? []) AppendLine(text, $"[{action.Label}]");
        }
        return ApplySenderAttribution(text.ToString().Trim(), document.SenderDisplayName, string.Empty, ":");
    }

    public static void Validate(ChannelSemanticMessageAction action, ChannelProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.Equals(action.Document.Version, ChannelSemanticContractVersions.MessageDocument, StringComparison.Ordinal))
            throw new InvalidOperationException("unsupported_semantic_document_version");
        if (string.IsNullOrWhiteSpace(action.ConversationId)) throw new InvalidOperationException("conversation_id_required");
        if (action.Document.Blocks.Count == 0) throw new InvalidOperationException("semantic_blocks_required");
        if (capabilities.ProviderKey == "slack") ValidateSlackNativeLimits(action.Document);

        HashSet<string> actionIds = new(StringComparer.Ordinal);
        int actionCount = 0;
        ChannelSemanticBlock[] allBlocks = FlattenBlocks(action.Document.Blocks).ToArray();
        foreach (ChannelSemanticBlock block in allBlocks)
        {
            if (!knownBlockTypes.Contains(block.Type)) throw new InvalidOperationException($"unsupported_semantic_block:{block.Type}");
            if (block.ImageUrl is not null
                && (!block.ImageUrl.IsAbsoluteUri
                    || !string.Equals(block.ImageUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(block.ImageUrl.UserInfo)
                    || !string.IsNullOrEmpty(block.ImageUrl.Query)
                    || !string.IsNullOrEmpty(block.ImageUrl.Fragment)))
                throw new InvalidOperationException("semantic_image_uri_not_safe");
            foreach (ChannelSemanticAction semanticAction in block.Actions ?? [])
            {
                actionCount++;
                if (!actionIds.Add(semanticAction.ActionId)) throw new InvalidOperationException("duplicate_semantic_action_id");
                if (!IsOpaqueToken(semanticAction.ActionId)) throw new InvalidOperationException("semantic_action_id_not_opaque");
                if (semanticAction.Label.Length > capabilities.MaximumActionLabelCharacters) throw new InvalidOperationException("semantic_action_label_too_long");
                if (Encoding.UTF8.GetByteCount(semanticAction.CallbackToken) > capabilities.MaximumCallbackTokenBytes) throw new InvalidOperationException("semantic_callback_token_too_long");
                if (!IsOpaqueToken(semanticAction.CallbackToken)) throw new InvalidOperationException("semantic_callback_token_not_opaque");
                if (semanticAction.Type is not (ChannelSemanticActionTypes.Button or ChannelSemanticActionTypes.Select or ChannelSemanticActionTypes.Input)) throw new InvalidOperationException("unsupported_semantic_action");
                if (semanticAction.Required && string.IsNullOrWhiteSpace(semanticAction.CallbackToken)) throw new InvalidOperationException("required_semantic_action_missing_callback");
                if ((semanticAction.Options?.Count ?? 0) > capabilities.MaximumSelectOptions) throw new InvalidOperationException("semantic_select_option_limit_exceeded");
                foreach (ChannelSemanticOption option in semanticAction.Options ?? [])
                {
                    if (Encoding.UTF8.GetByteCount(option.Value) > capabilities.MaximumCallbackTokenBytes) throw new InvalidOperationException("semantic_option_value_too_long");
                    if (!IsOpaqueToken(option.Value)) throw new InvalidOperationException("semantic_option_value_not_opaque");
                }
            }
        }
        if (actionCount > capabilities.MaximumActions) throw new InvalidOperationException("semantic_action_limit_exceeded");
        if (allBlocks.Length > capabilities.MaximumBlocks) throw new InvalidOperationException("semantic_block_limit_exceeded");
        if (CountActionRows(action.Document, capabilities) > capabilities.MaximumActionRows) throw new InvalidOperationException("semantic_action_row_limit_exceeded");
        bool securityCritical = !string.IsNullOrWhiteSpace(action.Document.TerminalState)
            || allBlocks.SelectMany(static block => block.Actions ?? []).Any(static item => item.Required);
        if (securityCritical && GetFallbackText(action.Document).Length > capabilities.MaximumMessageCharacters)
            throw new InvalidOperationException("semantic_security_critical_content_too_large");
    }

    private static void ValidateSlackNativeLimits(ChannelSemanticMessageDocument document)
    {
        foreach (ChannelSemanticBlock block in document.Blocks)
        {
            int textLength = block.Text?.Length ?? 0;
            if (block.Type is ChannelSemanticBlockTypes.Header or ChannelSemanticBlockTypes.Heading && textLength > 150)
                throw new InvalidOperationException("slack_header_text_limit_exceeded");
            if (block.Type is ChannelSemanticBlockTypes.Section or ChannelSemanticBlockTypes.Context && textLength > 3_000)
                throw new InvalidOperationException("slack_section_text_limit_exceeded");
            if ((block.Fields?.Count ?? 0) > 10) throw new InvalidOperationException("slack_section_field_limit_exceeded");
            if ((block.Fields ?? []).Any(static field => field.Label.Length + field.Value.Length + 2 > 2_000))
                throw new InvalidOperationException("slack_field_text_limit_exceeded");
            if ((block.Actions?.Count ?? 0) > 25) throw new InvalidOperationException("slack_actions_block_limit_exceeded");
        }
    }

    private static readonly HashSet<string> knownBlockTypes =
    [
        ChannelSemanticBlockTypes.Header, ChannelSemanticBlockTypes.Heading, ChannelSemanticBlockTypes.Section, ChannelSemanticBlockTypes.Fields,
        ChannelSemanticBlockTypes.Context, ChannelSemanticBlockTypes.Details, ChannelSemanticBlockTypes.Divider, ChannelSemanticBlockTypes.Image,
        ChannelSemanticBlockTypes.Table, ChannelSemanticBlockTypes.Actions,
    ];

    private static JsonObject SlackTextSection(string? value) => new() { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = value ?? string.Empty } };
    private static JsonObject SlackContextBlock(string? value) => new()
    {
        ["type"] = "context",
        ["elements"] = new JsonArray(new JsonObject { ["type"] = "mrkdwn", ["text"] = value ?? string.Empty }),
    };
    private static string SlackMrkdwn(string? value, bool preserveMarkdown)
    {
        string escaped = preserveMarkdown ? value ?? string.Empty : EscapeSlack(value);
        // Slack mrkdwn uses single asterisks for bold; model responses commonly
        // use CommonMark's double-asterisk form. Keep the rest of Slack's native
        // mrkdwn syntax (lists, quotes, links and code spans) intact.
        return System.Text.RegularExpressions.Regex.Replace(escaped, @"\*\*(.+?)\*\*", "*$1*", System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    private static JsonObject RenderSlackActions(IReadOnlyList<ChannelSemanticAction> actions)
    {
        JsonArray elements = [];
        foreach (ChannelSemanticAction action in actions)
        {
            if (action.Type == ChannelSemanticActionTypes.Select)
            {
                elements.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "static_select",
                    ["action_id"] = action.ActionId,
                    ["placeholder"] = new JsonObject { ["type"] = "plain_text", ["text"] = action.Label },
                    ["options"] = new JsonArray([.. (action.Options ?? []).Select(option => (JsonNode?)new JsonObject { ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = option.Label }, ["value"] = option.Value })]),
                });
            }
            else
            {
                JsonObject button = new() { ["type"] = "button", ["action_id"] = action.ActionId, ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = action.Label }, ["value"] = action.CallbackToken };
                if (action.Style == ChannelSemanticStyles.Primary) button["style"] = "primary";
                if (action.Style == ChannelSemanticStyles.Danger) button["style"] = "danger";
                elements.Add((JsonNode?)button);
            }
        }
        return new JsonObject { ["type"] = "actions", ["elements"] = elements };
    }

    private static string RenderTelegramRichActions(IReadOnlyList<ChannelSemanticAction> actions, bool inline = false)
    {
        var buttons = new List<string>();
        foreach (ChannelSemanticAction action in actions)
        {
            if (action.Type == ChannelSemanticActionTypes.Select)
            {
                foreach (ChannelSemanticOption option in action.Options ?? [])
                    buttons.Add(RichButton(option.Label, option.Value, action.Style));
            }
            else buttons.Add(RichButton(action.Label, action.CallbackToken,
                action.ActionId == "approve" ? ChannelSemanticStyles.Success : action.Style));
        }
        return inline ? string.Join(' ', buttons) : string.Join('\n', buttons.Chunk(8).Select(row => "<tg-button-row>" + string.Join(' ', row) + "</tg-button-row>"));
    }

    private static string RichButton(string label, string token, string? style)
    {
        string nativeStyle = style switch
        {
            ChannelSemanticStyles.Primary => "primary",
            ChannelSemanticStyles.Success => "success",
            ChannelSemanticStyles.Danger => "danger",
            _ => "link",
        };
        return $"<tg-button type=\"callback_data\" style=\"{nativeStyle}\" data=\"{System.Net.WebUtility.HtmlEncode(token)}\">{System.Net.WebUtility.HtmlEncode(label)}</tg-button>";
    }

    private static void AppendLine(StringBuilder builder, string? value) { if (!string.IsNullOrWhiteSpace(value)) builder.AppendLine(value); }
    private static string ApplySenderAttribution(string text, string? sender, string prefix, string suffix) => string.IsNullOrWhiteSpace(sender) ? text : $"{prefix}{sender}{suffix}\n{text}";
    /// <summary>Escapes data text for Telegram Rich Markdown while optionally preserving authored Markdown.</summary>
    internal static string EscapeTelegramMarkdown(string? value, bool preserveMarkdown = false)
    {
        StringBuilder escaped = new();
        foreach (char character in value ?? string.Empty)
        {
            if (!preserveMarkdown && character == '&')
            {
                escaped.Append("&amp;");
                continue;
            }
            if (character == '<') { escaped.Append("&lt;"); continue; }
            if (!preserveMarkdown && character == '>') { escaped.Append("&gt;"); continue; }

            // Keep authored Markdown intact. For data text, neutralize Markdown
            // link, code-span, and table delimiters. Raw HTML is always inert.
            if (!preserveMarkdown && character is ('[' or ']' or '|' or '`'))
            {
                escaped.Append("&#x").Append(((int)character).ToString("X", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                continue;
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    private static string RenderTelegramMarkdownTable(ChannelSemanticBlock block)
    {
        IReadOnlyList<string> headers = block.TableHeaders ?? [];
        if (headers.Count == 0)
        {
            return string.Join(Environment.NewLine, (block.TableRows ?? []).Select(row =>
                string.Join(" | ", row.Cells.Select(static cell => EscapeTelegramMarkdown(cell)))));
        }

        StringBuilder result = new();
        result.Append('|').AppendJoin('|', headers.Select(static header => EscapeTelegramMarkdown(header))).AppendLine("|");
        result.Append('|').AppendJoin('|', headers.Select(static _ => "---")).AppendLine("|");
        foreach (ChannelSemanticTableRow row in block.TableRows ?? [])
            result.Append('|').AppendJoin('|', row.Cells.Select(static cell => EscapeTelegramMarkdown(cell))).AppendLine("|");
        return result.ToString().TrimEnd();
    }
    private static string EscapeSlack(string? value) => (value ?? string.Empty).Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    private static string EscapeHtml(string? value) => EscapeSlack(value);
    private static string EscapeHtmlAttribute(string? value) => EscapeHtml(value).Replace("\"", "&quot;", StringComparison.Ordinal);
    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..Math.Max(0, maximum - 1)] + "…";
    private static int CountActionRows(ChannelSemanticMessageDocument document, ChannelProviderCapabilities capabilities)
    {
        if (capabilities.ProviderKey == "slack")
            return FlattenBlocks(document.Blocks).Count(static block => (block.Actions?.Count ?? 0) > 0);
        if (capabilities.ProviderKey == "telegram")
            return FlattenBlocks(document.Blocks).Sum(static block => (block.Actions ?? []).Sum(static action => action.Type == ChannelSemanticActionTypes.Select ? action.Options?.Count ?? 0 : 1));

        int rows = 0;
        foreach (ChannelSemanticBlock block in document.Blocks)
        {
            if (capabilities.ProviderKey == "telegram" && block.Type == ChannelSemanticBlockTypes.Section) continue;
            int pendingButtons = 0;
            foreach (ChannelSemanticAction action in block.Actions ?? [])
            {
                if (action.Type == ChannelSemanticActionTypes.Select)
                {
                    if (pendingButtons > 0) { rows++; pendingButtons = 0; }
                    rows++;
                }
                else if (++pendingButtons == capabilities.MaximumActions)
                {
                    rows++;
                    pendingButtons = 0;
                }
            }
            if (pendingButtons > 0) rows++;
        }
        return rows;
    }

    private static string RenderMarkdownTable(ChannelSemanticBlock block)
    {
        IReadOnlyList<string> headers = block.TableHeaders ?? [];
        if (headers.Count == 0) return RenderPlainTable(block);
        StringBuilder result = new();
        result.Append('|').AppendJoin('|', headers.Select(EscapeTableCell)).AppendLine("|");
        result.Append('|').AppendJoin('|', headers.Select(static _ => "---")).AppendLine("|");
        foreach (ChannelSemanticTableRow row in block.TableRows ?? []) result.Append('|').AppendJoin('|', row.Cells.Select(EscapeTableCell)).AppendLine("|");
        return result.ToString().TrimEnd();
    }

    private static string RenderPlainTable(ChannelSemanticBlock block)
    {
        StringBuilder result = new();
        if ((block.TableHeaders?.Count ?? 0) > 0) result.AppendJoin(" | ", block.TableHeaders!).AppendLine();
        foreach (ChannelSemanticTableRow row in block.TableRows ?? []) result.AppendJoin(" | ", row.Cells).AppendLine();
        return result.ToString().TrimEnd();
    }

    private static string EscapeTableCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static bool IsOpaqueToken(string value) => !string.IsNullOrWhiteSpace(value) && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or ':' or '-');
}
