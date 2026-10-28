using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.GoogleMail;

/// <summary>
/// Newline-delimited MCP stdio host. Logs stay off stdout so they cannot corrupt the protocol.
/// </summary>
internal sealed class GmailMcpStdioServer
{
    private const string serverName = "Kautuka Google Mail";
    private const string serverVersion = "0.1.0";
    private const string instructions =
        "Use gmail_list and gmail_read to look at the signed-in Gmail or Google Workspace mailbox. gmail_read returns a short summary for you, the attachments on that message, and a view of the original message for the owner. Use gmail_read_attachment when you need one of those files. Use gmail_create_draft or gmail_update_draft to prepare unsent mail. "
        + "gmail_send sends the draft. Kautuka may ask the owner to approve that send before it runs. Do not tell the owner a message was sent until gmail_send returns sent:true. "
        + "Plain text only. If a tool says the mailbox is not signed in, ask the owner to use Sign in with Google on the Connectors page.";

    public static async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        await using (writer.ConfigureAwait(false))
        {
            using var handler = new SocketsHttpHandler();
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string? response = await HandleLineAsync(line, (name, arguments, token) =>
                {
                    return GmailToolExecutor.CallAsync(name, arguments, ReadToolEnvironment(), handler, token);
                }, cancellationToken).ConfigureAwait(false);

                if (response is not null)
                {
                    await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    internal static Dictionary<string, string> ReadToolEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        CopyEnvironment(environment, GoogleApp.AccessTokenEnvironmentKey);
        return environment;
    }

    private static void CopyEnvironment(Dictionary<string, string> environment, string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            environment[key] = value;
        }
    }

    internal static async Task<string?> HandleLineAsync(
        string line,
        Func<string, JsonElement, CancellationToken, Task<MailToolResult>> callTool,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return ErrorResponse(null, -32700, "Parse error");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string method = methodElement.GetString() ?? string.Empty;
            bool hasId = root.TryGetProperty("id", out JsonElement id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
            JsonElement arguments = default;
            if (root.TryGetProperty("params", out JsonElement parameters))
            {
                arguments = parameters;
            }

            if (!hasId)
            {
                return null;
            }

            JsonNode? result = method switch
            {
                "initialize" => Initialize(arguments),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(arguments, callTool, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
            return result is null
                ? ErrorResponse(id, -32601, $"Method not found: {method}")
                : SuccessResponse(id, result);
        }
    }

    private static JsonObject Initialize(JsonElement parameters)
    {
        string protocolVersion = "2024-11-05";
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("protocolVersion", out JsonElement version)
            && version.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(version.GetString()))
        {
            protocolVersion = version.GetString()!;
        }

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = serverName,
                ["version"] = serverVersion,
            },
            ["instructions"] = instructions,
        };
    }

    private static JsonObject ListTools()
    {
        var tools = new JsonArray();
        Add(tools, Tool(
            MailToolNames.List,
            "List recent inbox messages, or search the mailbox.",
            """
            {"type":"object","properties":{"search":{"type":"string","description":"Optional Gmail search. Words search the mailbox. from: and subject: are allowed."},"receivedAfter":{"type":"string","format":"date-time","description":"Only return messages received at or after this ISO-8601 time."},"maxResults":{"type":"integer","minimum":1,"maximum":25,"description":"How many messages to return. Defaults to 15."}},"additionalProperties":false}
            """,
            readOnly: true,
            destructive: false));
        Add(tools, Tool(
            MailToolNames.Read,
            "Read one message. Returns the letter and each attachment id, name, type, and size. The owner sees the original message.",
            """
            {"type":"object","properties":{"messageId":{"type":"string","description":"Id from gmail_list."}},"required":["messageId"],"additionalProperties":false}
            """,
            readOnly: true,
            destructive: false));
        Add(tools, Tool(
            MailToolNames.ReadAttachment,
            "Read one attachment from a message. Documents and text come back in full. The owner also gets the file.",
            """
            {"type":"object","properties":{"messageId":{"type":"string","description":"Id from gmail_list or gmail_read."},"attachmentId":{"type":"string","description":"Id from the attachments list on gmail_read. Use that id even when Gmail shows a different download id later."}},"required":["messageId","attachmentId"],"additionalProperties":false}
            """,
            readOnly: true,
            destructive: false));
        Add(tools, Tool(
            MailToolNames.CreateDraft,
            "Save a new unsent draft. This does not send mail.",
            """
            {"type":"object","properties":{"to":{"type":"array","items":{"type":"string"},"description":"Recipient email addresses."},"cc":{"type":"array","items":{"type":"string"}},"subject":{"type":"string"},"body":{"type":"string","description":"Plain text body."}},"required":["to","subject","body"],"additionalProperties":false}
            """,
            readOnly: false,
            destructive: false));
        Add(tools, Tool(
            MailToolNames.UpdateDraft,
            "Change an existing draft. This does not send mail.",
            """
            {"type":"object","properties":{"draftId":{"type":"string"},"to":{"type":"array","items":{"type":"string"}},"cc":{"type":"array","items":{"type":"string"}},"subject":{"type":"string"},"body":{"type":"string"}},"required":["draftId"],"additionalProperties":false}
            """,
            readOnly: false,
            destructive: false));
        Add(tools, Tool(
            MailToolNames.Send,
            "Send mail only after the owner approves it in Kautuka. Sends exactly the approved recipients, subject, and body. Pass draftId to send a saved draft only when it still matches that text.",
            """
            {"type":"object","properties":{"to":{"type":"array","items":{"type":"string"}},"cc":{"type":"array","items":{"type":"string"}},"subject":{"type":"string"},"body":{"type":"string"},"draftId":{"type":"string","description":"Optional saved draft to send, if it matches to, subject, and body."}},"required":["to","subject","body"],"additionalProperties":false}
            """,
            readOnly: false,
            destructive: true));
        return new JsonObject
        {
            ["tools"] = tools,
        };
    }

    private static void Add(JsonArray array, JsonNode? node)
        => array.Add(node);

    private static JsonObject Tool(string name, string description, string inputSchema, bool readOnly, bool destructive)
        => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = JsonNode.Parse(inputSchema),
            ["annotations"] = new JsonObject
            {
                ["title"] = name,
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = destructive,
                ["openWorldHint"] = true,
            },
        };

    private static async Task<JsonObject> CallToolAsync(
        JsonElement parameters,
        Func<string, JsonElement, CancellationToken, Task<MailToolResult>> callTool,
        CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            return ToolError("Tool name is required.");
        }

        JsonElement arguments = default;
        if (parameters.TryGetProperty("arguments", out JsonElement argumentElement) && argumentElement.ValueKind == JsonValueKind.Object)
        {
            arguments = argumentElement;
        }

        MailToolResult result = await callTool(nameElement.GetString()!, arguments, cancellationToken).ConfigureAwait(false);
        return TextResult(result.Text, result.IsError, result.StructuredJson);
    }

    private static JsonObject ToolError(string message)
        => TextResult(message, isError: true, structuredJson: null);

    private static JsonObject TextResult(string message, bool isError, string? structuredJson)
    {
        var content = new JsonArray();
        Add(content, new JsonObject
        {
            ["type"] = "text",
            ["text"] = message,
        });
        var result = new JsonObject
        {
            ["content"] = content,
            ["isError"] = isError,
        };
        if (!string.IsNullOrWhiteSpace(structuredJson))
        {
            result["structuredContent"] = JsonNode.Parse(structuredJson);
        }

        return result;
    }

    private static string SuccessResponse(JsonElement id, JsonNode result)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = result,
        };
        return payload.ToJsonString();
    }

    private static string ErrorResponse(JsonElement? id, int code, string message)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is JsonElement present && present.ValueKind is JsonValueKind.Number or JsonValueKind.String
                ? JsonNode.Parse(present.GetRawText())
                : null,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        return payload.ToJsonString();
    }
}
