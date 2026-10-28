using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.M365Mail;

internal sealed class GraphMailClient : IDisposable
{
    private const string graphRoot = "https://graph.microsoft.com/v1.0/me";
    private const int maxAttachmentBytes = 32 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly string accessToken;

    public GraphMailClient(string accessToken, HttpMessageHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(handler);
        this.accessToken = accessToken;
        http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose() => http.Dispose();

    public async Task<JsonArray> ListAsync(string? search, int maxResults, CancellationToken cancellationToken)
        => await ListAsync(search, receivedAfter: null, maxResults, cancellationToken).ConfigureAwait(false);

    public async Task<JsonArray> ListAsync(string? search, string? receivedAfter, int maxResults, CancellationToken cancellationToken)
    {
        int top = Math.Clamp(maxResults, 1, 25);
        string? receivedFilter = null;
        DateTimeOffset? receivedAfterUtc = null;
        if (!string.IsNullOrWhiteSpace(receivedAfter))
        {
            if (!DateTimeOffset.TryParse(receivedAfter, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {
                throw new MailToolInputException("receivedAfter must be an ISO-8601 date and time.");
            }
            receivedAfterUtc = parsed.ToUniversalTime();
            receivedFilter = $"receivedDateTime ge {parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}";
        }
        bool hasSearch = !string.IsNullOrWhiteSpace(search);
        var query = new List<string>();
        if (hasSearch)
        {
            string searchQuery = SanitizeSearch(search!);
            if (receivedAfterUtc is { } searchAfter)
            {
                searchQuery += " AND received>=" + searchAfter.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }
            searchQuery = "\"" + searchQuery.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            query.Add($"$search={Uri.EscapeDataString(searchQuery)}");
            query.Add($"$top={top.ToString(CultureInfo.InvariantCulture)}");
            query.Add("$select=id,subject,from,receivedDateTime,isRead,bodyPreview");
        }
        else
        {
            query.Add($"$top={top.ToString(CultureInfo.InvariantCulture)}");
            query.Add("$select=id,subject,from,receivedDateTime,isRead,bodyPreview");
            if (receivedFilter is not null) query.Add($"$filter={Uri.EscapeDataString(receivedFilter)}");
            query.Add("$orderby=receivedDateTime desc");
        }
        string collection = hasSearch ? "/messages" : "/mailFolders/inbox/messages";
        string url = $"{graphRoot}{collection}?{string.Join('&', query)}";

        using JsonDocument document = await SendGraphAsync(HttpMethod.Get, url, null, hasSearch, cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        if (!document.RootElement.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement message in value.EnumerateArray())
        {
            if (receivedAfterUtc is { } after && message.TryGetProperty("receivedDateTime", out JsonElement received)
                && received.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(received.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset messageReceived)
                && messageReceived < after) continue;
            items.Add((JsonNode)Summarize(message, includeBody: false));
        }

        return items;
    }

    public async Task<JsonObject> ReadAsync(string messageId, CancellationToken cancellationToken)
    {
        string url = $"{graphRoot}/messages/{Uri.EscapeDataString(RequireGraphId(messageId, "messageId"))}?$select=id,subject,from,toRecipients,ccRecipients,receivedDateTime,isDraft,bodyPreview,body";
        using JsonDocument document = await SendGraphAsync(HttpMethod.Get, url, null, search: false, cancellationToken, htmlBody: true).ConfigureAwait(false);
        JsonObject summary = Summarize(document.RootElement, includeBody: true);
        summary["attachments"] = await ListAttachmentsAsync(messageId, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    public async Task<JsonObject> ReadPlainAsync(string messageId, CancellationToken cancellationToken)
    {
        string url = $"{graphRoot}/messages/{Uri.EscapeDataString(RequireGraphId(messageId, "messageId"))}?$select=id,subject,from,toRecipients,ccRecipients,receivedDateTime,isDraft,bodyPreview,body";
        using JsonDocument document = await SendGraphAsync(HttpMethod.Get, url, null, search: false, cancellationToken, htmlBody: false).ConfigureAwait(false);
        return Summarize(document.RootElement, includeBody: true);
    }

    private async Task<JsonObject> ReadDraftForSendAsync(string draftId, CancellationToken cancellationToken)
    {
        string url = $"{graphRoot}/messages/{Uri.EscapeDataString(RequireGraphId(draftId, "draftId"))}?$select=id,subject,toRecipients,ccRecipients,bccRecipients,isDraft,hasAttachments,body&$expand=attachments($select=id,isInline)";
        using JsonDocument document = await SendGraphAsync(HttpMethod.Get, url, null, search: false, cancellationToken).ConfigureAwait(false);
        return SummarizeDraftForSend(document.RootElement);
    }

    public async Task<JsonObject> ReadAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        string message = RequireGraphId(messageId, "messageId");
        string attachment = RequireGraphId(attachmentId, "attachmentId");
        string metadataUrl = $"{graphRoot}/messages/{Uri.EscapeDataString(message)}/attachments/{Uri.EscapeDataString(attachment)}?$select=id,name,contentType,size,microsoft.graph.fileAttachment/contentId";
        using JsonDocument metadata = await SendGraphAsync(HttpMethod.Get, metadataUrl, null, search: false, cancellationToken).ConfigureAwait(false);
        JsonElement item = metadata.RootElement;
        string? kind = ReadString(item, "@odata.type");
        if (kind is not null
            && (kind.Contains("itemAttachment", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("referenceAttachment", StringComparison.OrdinalIgnoreCase)))
        {
            throw new MailClientException("That attachment is not a file. Kautuka can download file attachments only.");
        }

        long declaredSize = ReadInt64(item, "size");
        if (declaredSize > maxAttachmentBytes)
        {
            throw new MailClientException("That attachment is too large to download. The limit is 32 MB.");
        }

        string contentUrl = $"{graphRoot}/messages/{Uri.EscapeDataString(message)}/attachments/{Uri.EscapeDataString(attachment)}/$value";
        byte[] bytes = await ReadCappedBytesAsync(contentUrl, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new MailClientException("That attachment is empty.");
        }

        string fileName = SafeFileName(ReadString(item, "name"));
        string contentType = ReadString(item, "contentType") ?? "application/octet-stream";
        return new JsonObject
        {
            ["fileName"] = fileName,
            ["contentType"] = contentType,
            ["sizeBytes"] = bytes.Length,
            ["contentId"] = ReadString(item, "contentId") ?? string.Empty,
            ["base64"] = Convert.ToBase64String(bytes),
        };
    }

    private async Task<JsonArray> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken)
    {
        string url = $"{graphRoot}/messages/{Uri.EscapeDataString(RequireGraphId(messageId, "messageId"))}/attachments?$select=id,name,contentType,size,isInline,microsoft.graph.fileAttachment/contentId";
        using JsonDocument document = await SendGraphAsync(HttpMethod.Get, url, null, search: false, cancellationToken).ConfigureAwait(false);
        var attachments = new JsonArray();
        if (!document.RootElement.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return attachments;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            attachments.Add((JsonNode)new JsonObject
            {
                ["id"] = id,
                ["name"] = SafeFileName(ReadString(item, "name")),
                ["contentType"] = ReadString(item, "contentType") ?? "application/octet-stream",
                ["size"] = ReadInt64(item, "size"),
                ["isInline"] = item.TryGetProperty("isInline", out JsonElement inline) && inline.ValueKind == JsonValueKind.True,
                ["contentId"] = ReadString(item, "contentId") ?? string.Empty,
            });
        }

        return attachments;
    }

    public async Task<JsonObject> CreateDraftAsync(MailMessageDraft draft, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendGraphAsync(HttpMethod.Post, $"{graphRoot}/messages", WriteMessage(draft), search: false, cancellationToken).ConfigureAwait(false);
        return Summarize(document.RootElement, includeBody: false);
    }

    public async Task<JsonObject> UpdateDraftAsync(string draftId, MailMessageDraft draft, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendGraphAsync(
            HttpMethod.Patch,
            $"{graphRoot}/messages/{Uri.EscapeDataString(draftId)}",
            WriteMessage(draft),
            search: false,
            cancellationToken).ConfigureAwait(false);
        return Summarize(document.RootElement, includeBody: false);
    }

    public async Task SendAsync(MailMessageDraft draft, string? draftId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(draftId))
        {
            JsonObject existing = await ReadDraftForSendAsync(draftId, cancellationToken).ConfigureAwait(false);
            EnsureDraftMatchesApproval(existing, draft);
            using JsonDocument _ = await SendGraphAsync(
                HttpMethod.Post,
                $"{graphRoot}/messages/{Uri.EscapeDataString(draftId)}/send",
                content: null,
                search: false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = new JsonObject
        {
            ["message"] = WriteMessage(draft),
            ["saveToSentItems"] = true,
        };
        using JsonDocument sent = await SendGraphAsync(HttpMethod.Post, $"{graphRoot}/sendMail", payload, search: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendGraphAsync(
        HttpMethod method,
        string url,
        JsonNode? content,
        bool search,
        CancellationToken cancellationToken,
        bool htmlBody = false)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            "Prefer",
            htmlBody ? "outlook.body-content-type=\"html\"" : "outlook.body-content-type=\"text\"");
        if (search)
        {
            request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
        }

        if (content is not null)
        {
            request.Content = new StringContent(content.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted || response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return JsonDocument.Parse("{}");
        }

        string body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MailClientException(ReadGraphError(body));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(body);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > 1_000_000)
                {
                    throw new MailClientException("Microsoft returned a message that is too large.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
    }

    private async Task<byte[]> ReadCappedBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            throw new MailClientException(ReadGraphError(body));
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maxAttachmentBytes)
                {
                    throw new MailClientException("That attachment is too large to download. The limit is 32 MB.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
    }

    private static string RequireGraphId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(static character => char.IsControl(character)))
        {
            throw new MailClientException(name == "attachmentId"
                ? "The attachment id is not valid."
                : "The message id is not valid.");
        }

        return value.Trim();
    }

    private static string SafeFileName(string? name)
    {
        string leaf = (name ?? string.Empty).Trim().Replace('\\', '/');
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        var builder = new StringBuilder(leaf.Length);
        foreach (char character in leaf)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        leaf = builder.ToString().Trim();
        if (leaf.Length == 0 || leaf is "." or "..")
        {
            return "attachment";
        }

        return leaf.Length > 180 ? leaf[..180] : leaf;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
    }

    private static void EnsureDraftMatchesApproval(JsonObject existing, MailMessageDraft approved)
    {
        if (existing["isDraft"] is JsonValue draftFlag && draftFlag.TryGetValue<bool>(out bool isDraft) && !isDraft)
        {
            throw new MailClientException("That message is not a draft, so it was not sent.");
        }

        string existingSubject = existing["subject"]?.GetValue<string>() ?? string.Empty;
        string existingBody = existing["body"]?.GetValue<string>() ?? string.Empty;
        if (existing["bcc"] is not JsonArray bcc
            || existing["hasAttachments"] is not JsonValue hasAttachmentsNode
            || !hasAttachmentsNode.TryGetValue<bool>(out bool hasAttachments)
            || hasAttachments
            || existing["hasInlineOrOtherAttachments"] is not JsonValue attachmentMetadataNode
            || !attachmentMetadataNode.TryGetValue<bool>(out bool hasInlineOrOtherAttachments)
            || hasInlineOrOtherAttachments
            || bcc.Count != 0
            || !string.Equals(existingSubject.Trim(), (approved.Subject ?? string.Empty).Trim(), StringComparison.Ordinal)
            || !string.Equals(existingBody.Trim(), (approved.Body ?? string.Empty).Trim(), StringComparison.Ordinal)
            || !AddressesMatch(existing["to"], approved.To ?? [])
            || !AddressesMatch(existing["cc"], approved.Cc ?? []))
        {
            throw new MailClientException("The draft does not match the message waiting for approval, so it was not sent.");
        }
    }

    private static bool AddressesMatch(JsonNode? existing, IReadOnlyList<string> approved)
    {
        var left = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is not null)
                {
                    left.Add(item.GetValue<string>());
                }
            }
        }

        var right = new HashSet<string>(approved, StringComparer.OrdinalIgnoreCase);
        return left.SetEquals(right);
    }

    private static JsonObject WriteMessage(MailMessageDraft draft)
    {
        var message = new JsonObject();
        if (draft.Subject is not null)
        {
            message["subject"] = draft.Subject;
        }

        if (draft.Body is not null)
        {
            message["body"] = new JsonObject
            {
                ["contentType"] = "Text",
                ["content"] = draft.Body,
            };
        }

        if (draft.To is not null)
        {
            message["toRecipients"] = WriteRecipients(draft.To);
        }

        if (draft.Cc is not null)
        {
            message["ccRecipients"] = WriteRecipients(draft.Cc);
        }

        return message;
    }

    private static JsonObject SummarizeDraftForSend(JsonElement message)
    {
        JsonObject summary = Summarize(message, includeBody: true);
        if (!message.TryGetProperty("bccRecipients", out JsonElement bcc)
            || bcc.ValueKind != JsonValueKind.Array
            || !message.TryGetProperty("hasAttachments", out JsonElement hasAttachments)
            || hasAttachments.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !message.TryGetProperty("attachments", out JsonElement attachments)
            || attachments.ValueKind != JsonValueKind.Array
            || message.TryGetProperty("attachments@odata.nextLink", out _))
        {
            throw new MailClientException("Microsoft could not confirm the complete draft details, so it was not sent.");
        }

        var bccAddresses = new JsonArray();
        foreach (JsonElement recipient in bcc.EnumerateArray())
        {
            if (recipient.ValueKind != JsonValueKind.Object
                || !recipient.TryGetProperty("emailAddress", out JsonElement email)
                || email.ValueKind != JsonValueKind.Object
                || !email.TryGetProperty("address", out JsonElement addressNode)
                || addressNode.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(addressNode.GetString())
                || addressNode.GetString()!.Any(char.IsControl))
            {
                throw new MailClientException("Microsoft returned a mail address that could not be read.");
            }

            bccAddresses.Add((JsonNode?)JsonValue.Create(addressNode.GetString()));
        }

        summary["bcc"] = bccAddresses;
        summary["hasAttachments"] = hasAttachments.ValueKind == JsonValueKind.True;
        summary["hasInlineOrOtherAttachments"] = attachments.GetArrayLength() > 0;
        return summary;
    }

    private static JsonArray WriteRecipients(IReadOnlyList<string> addresses)
    {
        var recipients = new JsonArray();
        foreach (string address in addresses)
        {
            if (address.Any(char.IsControl))
            {
                throw new MailClientException("The recipient list is not valid.");
            }

            recipients.Add((JsonNode)new JsonObject
            {
                ["emailAddress"] = new JsonObject { ["address"] = address },
            });
        }

        return recipients;
    }

    private static JsonObject Summarize(JsonElement message, bool includeBody)
    {
        var summary = new JsonObject
        {
            ["id"] = ReadString(message, "id") ?? string.Empty,
            ["subject"] = ReadString(message, "subject") ?? string.Empty,
            ["from"] = ReadFrom(message),
            ["received"] = ReadString(message, "receivedDateTime") ?? string.Empty,
            ["isRead"] = message.TryGetProperty("isRead", out JsonElement isRead) && isRead.ValueKind == JsonValueKind.True,
            ["preview"] = ReadString(message, "bodyPreview") ?? string.Empty,
        };
        if (message.TryGetProperty("isDraft", out JsonElement isDraft))
        {
            summary["isDraft"] = isDraft.ValueKind == JsonValueKind.True;
        }

        summary["to"] = ReadAddresses(message, "toRecipients");
        summary["cc"] = ReadAddresses(message, "ccRecipients");
        if (includeBody)
        {
            string body = string.Empty;
            string? format = null;
            if (message.TryGetProperty("body", out JsonElement bodyElement))
            {
                body = ReadString(bodyElement, "content") ?? string.Empty;
                format = ReadString(bodyElement, "contentType");
            }

            if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
            {
                summary["html"] = body;
                string plain = MailHtmlText.ToPlain(body);
                summary["body"] = plain.Length > 12000 ? plain[..12000] : plain;
                summary["bodyTruncated"] = plain.Length > 12000;
                summary["bodyFormat"] = "html";
            }
            else if (body.Length > 8000)
            {
                summary["body"] = body[..8000];
                summary["bodyTruncated"] = true;
                summary["bodyFormat"] = "text";
            }
            else
            {
                summary["body"] = body;
                summary["bodyTruncated"] = false;
                summary["bodyFormat"] = "text";
            }
        }

        return summary;
    }

    private static string ReadFrom(JsonElement message)
    {
        if (!message.TryGetProperty("from", out JsonElement from))
        {
            return string.Empty;
        }

        if (!from.TryGetProperty("emailAddress", out JsonElement email))
        {
            return string.Empty;
        }

        return ReadString(email, "address") ?? string.Empty;
    }

    private static JsonArray ReadAddresses(JsonElement message, string propertyName)
    {
        var addresses = new JsonArray();
        if (!message.TryGetProperty(propertyName, out JsonElement recipients) || recipients.ValueKind != JsonValueKind.Array)
        {
            return addresses;
        }

        foreach (JsonElement recipient in recipients.EnumerateArray())
        {
            if (!recipient.TryGetProperty("emailAddress", out JsonElement email))
            {
                continue;
            }

            string? address = ReadString(email, "address");
            if (!string.IsNullOrWhiteSpace(address))
            {
                if (address.Any(char.IsControl))
                {
                    throw new MailClientException("Microsoft returned a mail address that could not be read.");
                }

                JsonNode? node = JsonValue.Create(address);
                addresses.Add(node);
            }
        }

        return addresses;
    }

    private static string ReadGraphError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                string? message = ReadString(error, "message");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return TrimGraphMessage(message);
                }
            }
        }
        catch (JsonException)
        {
            return "Microsoft rejected the mail request.";
        }

        return "Microsoft rejected the mail request.";
    }

    private static string TrimGraphMessage(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length > 300)
        {
            trimmed = trimmed[..300];
        }

        if (trimmed.Contains("eyJ", StringComparison.Ordinal)
            || trimmed.Contains("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft rejected the mail request.";
        }

        return trimmed;
    }

    private static string SanitizeSearch(string search)
    {
        string trimmed = search.Trim().Replace("\"", string.Empty, StringComparison.Ordinal);
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed record MailMessageDraft(IReadOnlyList<string>? To, IReadOnlyList<string>? Cc, string? Subject, string? Body);
