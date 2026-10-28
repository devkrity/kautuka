using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.M365Mail;

internal static class MailToolNames
{
    public const string List = "mail_list";
    public const string Read = "mail_read";
    public const string ReadAttachment = "mail_read_attachment";
    public const string CreateDraft = "mail_create_draft";
    public const string UpdateDraft = "mail_update_draft";
    public const string Send = MicrosoftApp.SendToolName;
}

internal readonly record struct MailToolResult(bool IsError, string Text, string? StructuredJson = null);

internal static class MailToolExecutor
{
    public static async Task<MailToolResult> CallAsync(
        string name,
        JsonElement arguments,
        IReadOnlyDictionary<string, string> environment,
        HttpMessageHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(handler);

        try
        {
            string? accessToken = environment.TryGetValue(MicrosoftApp.AccessTokenEnvironmentKey, out string? token)
                ? token
                : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new MailToolResult(true, MicrosoftApp.SignInRequiredMessage);
            }

            using var client = new GraphMailClient(accessToken, handler);
            if (name == MailToolNames.Read)
            {
                return await ReadAsync(client, RequireText(arguments, "messageId"), cancellationToken).ConfigureAwait(false);
            }

            if (name == MailToolNames.List)
            {
                JsonArray messages = await client.ListAsync(
                    ReadOptionalText(arguments, "search"),
                    ReadOptionalText(arguments, "receivedAfter"),
                    ReadMaxResults(arguments),
                    cancellationToken).ConfigureAwait(false);
                return MailView.FromList(messages);
            }

            if (name == MailToolNames.ReadAttachment)
            {
                JsonObject file = await client.ReadAttachmentAsync(
                    RequireText(arguments, "messageId"),
                    RequireText(arguments, "attachmentId"),
                    cancellationToken).ConfigureAwait(false);
                byte[] bytes = Convert.FromBase64String(file["base64"]?.GetValue<string>() ?? string.Empty);
                return MailView.FromAttachment(
                    file["fileName"]?.GetValue<string>() ?? "attachment",
                    file["contentType"]?.GetValue<string>() ?? "application/octet-stream",
                    bytes);
            }

            if (name == MailToolNames.CreateDraft)
            {
                MailMessageDraft draft = ReadDraft(arguments, requireBody: true);
                JsonObject created = await client.CreateDraftAsync(draft, cancellationToken).ConfigureAwait(false);
                return new MailToolResult(false, created.ToJsonString());
            }

            if (name == MailToolNames.UpdateDraft)
            {
                MailMessageDraft draft = ReadDraft(arguments, requireBody: false);
                JsonObject updated = await client.UpdateDraftAsync(RequireText(arguments, "draftId"), draft, cancellationToken).ConfigureAwait(false);
                return new MailToolResult(false, updated.ToJsonString());
            }

            if (name == MailToolNames.Send)
            {
                return await SendAsync(client, arguments, cancellationToken).ConfigureAwait(false);
            }

            throw new MailToolInputException($"Unknown mail tool '{name}'.");
        }
        catch (MailToolInputException ex)
        {
            return new MailToolResult(true, ex.Message);
        }
        catch (MailClientException ex)
        {
            return new MailToolResult(true, ex.Message);
        }
        catch (HttpRequestException)
        {
            return new MailToolResult(true, "Could not reach Microsoft. Check the appliance network and try again.");
        }
        catch (JsonException)
        {
            return new MailToolResult(true, "Microsoft returned a mail response that could not be read.");
        }
    }

    private static async Task<MailToolResult> ReadAsync(GraphMailClient client, string messageId, CancellationToken cancellationToken)
    {
        JsonObject read = await client.ReadAsync(messageId, cancellationToken).ConfigureAwait(false);
        var files = new List<DownloadedMailFile>();
        if (read["attachments"] is JsonArray attachments)
        {
            foreach (JsonNode? node in attachments)
            {
                if (node is not JsonObject attachment)
                {
                    continue;
                }

                string? attachmentId = attachment["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(attachmentId))
                {
                    continue;
                }

                try
                {
                    JsonObject file = await client.ReadAttachmentAsync(messageId, attachmentId, cancellationToken).ConfigureAwait(false);
                    string? contentId = file["contentId"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(contentId))
                    {
                        contentId = attachment["contentId"]?.GetValue<string>();
                    }

                    files.Add(new DownloadedMailFile(
                        attachmentId,
                        file["fileName"]?.GetValue<string>() ?? attachment["name"]?.GetValue<string>() ?? "attachment",
                        file["contentType"]?.GetValue<string>() ?? "application/octet-stream",
                        contentId,
                        Convert.FromBase64String(file["base64"]?.GetValue<string>() ?? string.Empty)));
                }
                catch (MailClientException ex)
                {
                    attachment["downloadError"] = ex.Message;
                }
            }
        }

        return MailView.FromRead(read, files);
    }

    private static int ReadMaxResults(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("maxResults", out JsonElement maxElement)
            && maxElement.TryGetInt32(out int requested))
        {
            return requested;
        }

        return 15;
    }

    private static async Task<MailToolResult> SendAsync(GraphMailClient client, JsonElement arguments, CancellationToken cancellationToken)
    {
        MailMessageDraft draft = ReadDraft(arguments, requireBody: true);
        string? draftId = ReadOptionalText(arguments, "draftId");
        await client.SendAsync(draft, draftId, cancellationToken).ConfigureAwait(false);
        var model = new JsonObject
        {
            ["sent"] = true,
            ["draftId"] = draftId ?? string.Empty,
            ["to"] = ToJsonArray(draft.To ?? []),
            ["subject"] = draft.Subject ?? string.Empty,
        };
        return new MailToolResult(false, model.ToJsonString());
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            JsonNode? node = JsonValue.Create(value);
            array.Add(node);
        }

        return array;
    }

    private static MailMessageDraft ReadDraft(JsonElement arguments, bool requireBody)
    {
        bool hasTo = HasProperty(arguments, "to");
        bool hasCc = HasProperty(arguments, "cc");
        string[]? to = hasTo || requireBody ? ReadAddresses(arguments, "to", required: requireBody) : null;
        string[]? cc = hasCc ? ReadAddresses(arguments, "cc", required: false) : null;
        string? subject = requireBody ? RequireText(arguments, "subject") : ReadOptionalText(arguments, "subject");
        string? body = requireBody ? RequireText(arguments, "body") : ReadOptionalText(arguments, "body");
        if (!requireBody && to is null && cc is null && subject is null && body is null)
        {
            throw new MailToolInputException("Say what to change: to, cc, subject, or body.");
        }

        if (subject is { Length: > 255 })
        {
            throw new MailToolInputException("Subject is too long.");
        }

        if (body is { Length: > 100_000 })
        {
            throw new MailToolInputException("Body is too long.");
        }

        return new MailMessageDraft(to, cc, subject, body);
    }

    private static bool HasProperty(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out _);

    private static string[] ReadAddresses(JsonElement arguments, string name, bool required)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
        {
            if (required)
            {
                throw new MailToolInputException($"{name} is required.");
            }

            return [];
        }

        var addresses = new List<string>();
        if (value.ValueKind == JsonValueKind.String)
        {
            addresses.Add(RequireAddress(value.GetString(), name));
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new MailToolInputException($"{name} must be email addresses.");
                }

                addresses.Add(RequireAddress(item.GetString(), name));
            }
        }
        else
        {
            throw new MailToolInputException($"{name} must be email addresses.");
        }

        if (required && addresses.Count == 0)
        {
            throw new MailToolInputException($"{name} is required.");
        }

        if (addresses.Count > 20)
        {
            throw new MailToolInputException($"{name} has too many addresses.");
        }

        return [.. addresses];
    }

    private static string RequireAddress(string? value, string name)
    {
        if (value?.Any(char.IsControl) == true)
        {
            throw new MailToolInputException($"{name} must be an email address.");
        }

        string address = value?.Trim() ?? string.Empty;

        int at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0
            || at != address.LastIndexOf('@')
            || at == address.Length - 1
            || address.Contains(' ', StringComparison.Ordinal)
            || !address[(at + 1)..].Contains('.', StringComparison.Ordinal))
        {
            throw new MailToolInputException($"{name} must be an email address.");
        }

        return address;
    }

    private static string RequireText(JsonElement arguments, string name)
    {
        string? value = ReadOptionalText(arguments, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MailToolInputException($"{name} is required.");
        }

        return value.Trim();
    }

    private static string? ReadOptionalText(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

internal sealed class MailToolInputException : InvalidOperationException
{
    public MailToolInputException()
    {
    }

    public MailToolInputException(string message)
        : base(message)
    {
    }

    public MailToolInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class MailClientException : InvalidOperationException
{
    public MailClientException()
    {
    }

    public MailClientException(string message)
        : base(message)
    {
    }

    public MailClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
