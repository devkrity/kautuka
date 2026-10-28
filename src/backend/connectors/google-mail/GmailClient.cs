using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.GoogleMail;

internal sealed class GmailClient : IDisposable
{
    private const string gmailRoot = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const int maxAttachmentBytes = 32 * 1024 * 1024;
    private const int maxMessageResponseBytes = 1_000_000;
    private const string attachmentTooLarge = "That attachment is too large to download. The limit is 32 MB.";
    private const int maxAttachmentResponseBytes = ((maxAttachmentBytes + 2) / 3 * 4) + 256;
    private readonly HttpClient http;
    private readonly string accessToken;

    public GmailClient(string accessToken, HttpMessageHandler handler)
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
        string query = SanitizeSearch(search ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(receivedAfter))
        {
            if (!DateTimeOffset.TryParse(receivedAfter, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {
                throw new MailToolInputException("receivedAfter must be an ISO-8601 date and time.");
            }
            long epochSeconds = parsed.ToUnixTimeSeconds();
            query = string.IsNullOrWhiteSpace(query) ? $"in:inbox after:{epochSeconds}" : $"{query} after:{epochSeconds}";
        }
        string listUrl = string.IsNullOrWhiteSpace(query)
            ? $"{gmailRoot}/messages?maxResults={top.ToString(CultureInfo.InvariantCulture)}&labelIds=INBOX"
            : $"{gmailRoot}/messages?maxResults={top.ToString(CultureInfo.InvariantCulture)}&q={Uri.EscapeDataString(query)}";
        using JsonDocument list = await SendAsync(HttpMethod.Get, listUrl, null, cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        if (!list.RootElement.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement message in messages.EnumerateArray())
        {
            string? id = ReadString(message, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            string metadataUrl = $"{gmailRoot}/messages/{Uri.EscapeDataString(RequireId(id, "messageId"))}?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Date";
            using JsonDocument metadata = await SendAsync(HttpMethod.Get, metadataUrl, null, cancellationToken).ConfigureAwait(false);
            items.Add((JsonNode)Summarize(metadata.RootElement, includeBody: false, plain: null, html: null));
        }

        return items;
    }

    public async Task<JsonObject> ReadAsync(string messageId, CancellationToken cancellationToken)
    {
        string url = $"{gmailRoot}/messages/{Uri.EscapeDataString(RequireId(messageId, "messageId"))}?format=full";
        using JsonDocument document = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        ParsedBody body = ParseBody(document.RootElement);
        JsonObject summary = Summarize(document.RootElement, includeBody: true, body.Plain, body.Html);
        var attachments = new JsonArray();
        foreach (GmailAttachment attachment in body.Attachments)
        {
            attachments.Add((JsonNode)new JsonObject
            {
                ["id"] = attachment.Id,
                ["name"] = attachment.Name,
                ["contentType"] = attachment.ContentType,
                ["size"] = attachment.Size,
                ["contentId"] = attachment.ContentId ?? string.Empty,
            });
        }

        summary["attachments"] = attachments;
        return summary;
    }

    public async Task<JsonObject> ReadAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        string url = $"{gmailRoot}/messages/{Uri.EscapeDataString(RequireId(messageId, "messageId"))}?format=full";
        using JsonDocument document = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        List<GmailAttachment> listed = ParseBody(document.RootElement).Attachments;
        GmailAttachment? match = null;
        foreach (GmailAttachment attachment in listed)
        {
            if (string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal)
                || string.Equals(attachment.GmailAttachmentId, attachmentId, StringComparison.Ordinal))
            {
                match = attachment;
                break;
            }
        }

        if (match is null)
        {
            byte[] stale = await DownloadAttachmentAsync(messageId, attachmentId, cancellationToken).ConfigureAwait(false);
            if (stale.Length == 0)
            {
                throw new MailClientException("That attachment is empty.");
            }

            return AttachmentResult("attachment", "application/octet-stream", string.Empty, stale);
        }

        if (match.Size > maxAttachmentBytes)
        {
            throw new MailClientException(attachmentTooLarge);
        }

        byte[] bytes = match.Inline ?? await DownloadAttachmentAsync(
            messageId,
            match.GmailAttachmentId ?? attachmentId,
            cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new MailClientException("That attachment is empty.");
        }

        return AttachmentResult(SafeFileName(match.Name), match.ContentType, match.ContentId ?? string.Empty, bytes);
    }

    public async Task<JsonObject> CreateDraftAsync(MailMessageDraft draft, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["raw"] = BuildRaw(draft),
            },
        };
        using JsonDocument document = await SendAsync(HttpMethod.Post, $"{gmailRoot}/drafts", payload, cancellationToken).ConfigureAwait(false);
        return DraftSummary(document.RootElement, draft);
    }

    public async Task<JsonObject> UpdateDraftAsync(string draftId, MailMessageDraft draft, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["raw"] = BuildRaw(draft),
            },
        };
        string url = $"{gmailRoot}/drafts/{Uri.EscapeDataString(RequireId(draftId, "draftId"))}";
        using JsonDocument document = await SendAsync(HttpMethod.Put, url, payload, cancellationToken).ConfigureAwait(false);
        return DraftSummary(document.RootElement, draft);
    }

    public async Task SendAsync(MailMessageDraft draft, string? draftId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(draftId))
        {
            string url = $"{gmailRoot}/drafts/{Uri.EscapeDataString(RequireId(draftId, "draftId"))}?format=full";
            using JsonDocument existing = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
            EnsureDraftMatchesApproval(existing.RootElement, draft);
            var sendDraft = new JsonObject
            {
                ["id"] = draftId,
            };
            using JsonDocument _ = await SendAsync(HttpMethod.Post, $"{gmailRoot}/drafts/send", sendDraft, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = new JsonObject
        {
            ["raw"] = BuildRaw(draft),
        };
        using JsonDocument sent = await SendAsync(HttpMethod.Post, $"{gmailRoot}/messages/send", payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> DownloadAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        string url = $"{gmailRoot}/messages/{Uri.EscapeDataString(RequireId(messageId, "messageId"))}/attachments/{Uri.EscapeDataString(RequireId(attachmentId, "attachmentId"))}";
        using JsonDocument document = await SendAsync(
            HttpMethod.Get,
            url,
            null,
            cancellationToken,
            maxAttachmentResponseBytes,
            attachmentTooLarge).ConfigureAwait(false);
        long declared = ReadInt64(document.RootElement, "size");
        if (declared > maxAttachmentBytes)
        {
            throw new MailClientException(attachmentTooLarge);
        }

        string? data = ReadString(document.RootElement, "data");
        byte[] bytes = DecodeBase64Url(data);
        if (bytes.Length > maxAttachmentBytes)
        {
            throw new MailClientException(attachmentTooLarge);
        }

        return bytes;
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string url,
        JsonNode? content,
        CancellationToken cancellationToken,
        int maxResponseBytes = maxMessageResponseBytes,
        string? tooLargeMessage = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (content is not null)
        {
            request.Content = new StringContent(content.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await ReadBoundedAsync(response, maxResponseBytes, tooLargeMessage, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MailClientException(ReadGmailError(body));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(body);
    }

    private static JsonObject DraftSummary(JsonElement document, MailMessageDraft draft)
    {
        var to = new JsonArray();
        foreach (string address in draft.To ?? [])
        {
            to.Add((JsonNode)JsonValue.Create(address)!);
        }

        return new JsonObject
        {
            ["id"] = ReadString(document, "id") ?? string.Empty,
            ["subject"] = draft.Subject ?? string.Empty,
            ["to"] = to,
        };
    }

    private static JsonObject Summarize(JsonElement message, bool includeBody, string? plain, string? html)
    {
        string subject = Header(message, "Subject");
        var summary = new JsonObject
        {
            ["id"] = ReadString(message, "id") ?? string.Empty,
            ["subject"] = subject,
            ["from"] = ExtractAddress(Header(message, "From")),
            ["received"] = ReadReceived(message),
            ["isRead"] = !HasLabel(message, "UNREAD"),
            ["preview"] = ReadString(message, "snippet") ?? string.Empty,
            ["to"] = ReadAddressList(Header(message, "To")),
            ["cc"] = ReadAddressList(Header(message, "Cc")),
        };
        if (HasLabel(message, "DRAFT"))
        {
            summary["isDraft"] = true;
        }

        if (includeBody)
        {
            if (!string.IsNullOrEmpty(html))
            {
                summary["html"] = html;
                string visible = MailHtmlText.ToPlain(html);
                summary["body"] = visible.Length > 12000 ? visible[..12000] : visible;
                summary["bodyTruncated"] = visible.Length > 12000;
                summary["bodyFormat"] = "html";
            }
            else
            {
                string body = plain ?? string.Empty;
                summary["body"] = body.Length > 8000 ? body[..8000] : body;
                summary["bodyTruncated"] = body.Length > 8000;
                summary["bodyFormat"] = "text";
            }
        }

        return summary;
    }

    private static void EnsureDraftMatchesApproval(JsonElement draft, MailMessageDraft approved)
    {
        if (!draft.TryGetProperty("message", out JsonElement message))
        {
            throw new MailClientException("That message is not a draft, so it was not sent.");
        }

        if (message.TryGetProperty("labelIds", out JsonElement labels)
            && labels.ValueKind == JsonValueKind.Array
            && !HasLabel(message, "DRAFT"))
        {
            throw new MailClientException("That message is not a draft, so it was not sent.");
        }

        ParsedBody body = ParseBody(message);
        string existingSubject = Header(message, "Subject").Trim();
        string approvedBody = (approved.Body ?? string.Empty).Trim();
        bool bodyMatches = (body.Plain is null || string.Equals(body.Plain.Trim(), approvedBody, StringComparison.Ordinal))
            && (body.Html is null || MailHtmlText.IsSafeForDraftApproval(body.Html)
                && string.Equals(MailHtmlText.ToPlain(body.Html).Trim(), approvedBody, StringComparison.Ordinal))
            && (body.Plain is not null || body.Html is not null || approvedBody.Length == 0);
        if (!string.Equals(existingSubject, (approved.Subject ?? string.Empty).Trim(), StringComparison.Ordinal)
            || !bodyMatches
            || !AddressesMatch(ReadAddressList(Header(message, "To")), approved.To ?? [])
            || !AddressesMatch(ReadAddressList(Header(message, "Cc")), approved.Cc ?? [])
            || ReadAddressList(Header(message, "Bcc")).Count != 0
            || body.HasAttachments)
        {
            throw new MailClientException("The draft does not match the message waiting for approval, so it was not sent.");
        }
    }

    private static bool AddressesMatch(JsonArray existing, IReadOnlyList<string> approved)
    {
        var left = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in existing)
        {
            string? address = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(address))
            {
                _ = left.Add(address);
            }
        }

        var right = new HashSet<string>(approved, StringComparer.OrdinalIgnoreCase);
        return left.SetEquals(right);
    }

    private static ParsedBody ParseBody(JsonElement message)
    {
        string? plain = null;
        string? html = null;
        bool hasAttachments = false;
        var attachments = new List<GmailAttachment>();
        if (message.TryGetProperty("payload", out JsonElement payload))
        {
            Walk(payload, attachments, ref plain, ref html, ref hasAttachments);
        }

        return new ParsedBody(plain, html, attachments, hasAttachments);
    }

    private static void Walk(JsonElement part, List<GmailAttachment> attachments, ref string? plain, ref string? html, ref bool hasAttachments)
    {
        if (part.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in parts.EnumerateArray())
            {
                Walk(child, attachments, ref plain, ref html, ref hasAttachments);
            }
        }

        string mime = ReadString(part, "mimeType") ?? "application/octet-stream";
        string? contentId = Header(part, "Content-ID");
        string disposition = Header(part, "Content-Disposition");
        string? rawName = ReadString(part, "filename");
        if (string.IsNullOrWhiteSpace(rawName))
        {
            rawName = FileNameFromDisposition(disposition);
        }

        bool named = !string.IsNullOrWhiteSpace(rawName);
        bool inline = !string.IsNullOrWhiteSpace(contentId);
        bool attached = IsAttachmentDisposition(disposition);
        bool fileType = IsSeparateFile(mime) || string.Equals(mime, "message/rfc822", StringComparison.OrdinalIgnoreCase);
        part.TryGetProperty("body", out JsonElement body);
        string? attachmentId = body.ValueKind == JsonValueKind.Object ? ReadString(body, "attachmentId") : null;
        string? inlineData = body.ValueKind == JsonValueKind.Object ? ReadString(body, "data") : null;
        // Only plain text, HTML, and multipart containers are body types. Every
        // other MIME type (including message/rfc822) is unsafe for an approved
        // text-only send, even when Gmail omits filename and body payload data.
        bool attachment = named || inline || attached || fileType || !string.IsNullOrWhiteSpace(attachmentId);
        if (attachment)
        {
            hasAttachments = true;
            if (!string.IsNullOrWhiteSpace(attachmentId))
            {
                attachments.Add(new GmailAttachment(
                    PublicPartId(part, attachments.Count),
                    FileNameFor(rawName, mime),
                    mime,
                    CleanContentId(contentId),
                    ReadInt64(body, "size"),
                    attachmentId));
            }
            else if (!string.IsNullOrWhiteSpace(inlineData))
            {
                byte[] bytes = DecodeBase64Url(inlineData);
                if (bytes.Length > maxAttachmentBytes)
                {
                    throw new MailClientException(attachmentTooLarge);
                }

                if (bytes.Length > 0)
                {
                    attachments.Add(new GmailAttachment(
                        PublicPartId(part, attachments.Count),
                        FileNameFor(rawName, mime),
                        mime,
                        CleanContentId(contentId),
                        bytes.Length,
                        null,
                        bytes));
                }
            }

            return;
        }

        if (part.TryGetProperty("body", out JsonElement textBody))
        {
            string decoded = Encoding.UTF8.GetString(DecodeBase64Url(ReadString(textBody, "data")));
            if (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                if (html is not null)
                {
                    throw new MailClientException("The draft does not match the approved content because it contains ambiguous HTML body parts, so it was not sent.");
                }
                html = decoded;
            }
            else if (mime.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                if (plain is not null)
                {
                    throw new MailClientException("The draft does not match the approved content because it contains ambiguous plain-text body parts, so it was not sent.");
                }
                plain = decoded;
            }
        }
    }

    private static string BuildRaw(MailMessageDraft draft)
    {
        ValidateDraftAddresses(draft.To);
        ValidateDraftAddresses(draft.Cc);
        var builder = new StringBuilder();
        _ = builder.Append("To: ").Append(string.Join(", ", draft.To ?? [])).Append("\r\n");
        if (draft.Cc is { Count: > 0 })
        {
            _ = builder.Append("Cc: ").Append(string.Join(", ", draft.Cc)).Append("\r\n");
        }

        _ = builder.Append("Subject: ").Append(EncodeHeader(draft.Subject ?? string.Empty)).Append("\r\n");
        _ = builder.Append("MIME-Version: 1.0\r\n");
        _ = builder.Append("Content-Type: text/plain; charset=utf-8\r\n");
        _ = builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        _ = builder.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(draft.Body ?? string.Empty)));
        return Base64Url(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void ValidateDraftAddresses(IReadOnlyList<string>? addresses)
    {
        foreach (string address in addresses ?? [])
        {
            try
            {
                if (address.Any(char.IsControl) || !IsValidRfc5322Address(new MailAddress(address).Address))
                {
                    throw new FormatException("Invalid address.");
                }
            }
            catch (FormatException)
            {
                throw new MailClientException("The recipient list is not valid.");
            }
        }
    }

    private static string EncodeHeader(string value)
    {
        if (value.Any(static character => character is '\r' or '\n'))
        {
            throw new MailClientException("Subject is not valid.");
        }

        foreach (char character in value)
        {
            if (character > 127)
            {
                return "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";
            }
        }

        return value;
    }

    private static string ReadReceived(JsonElement message)
    {
        if (message.TryGetProperty("internalDate", out JsonElement internalDate)
            && internalDate.ValueKind == JsonValueKind.String
            && long.TryParse(internalDate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long millis))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis).ToString("o", CultureInfo.InvariantCulture);
        }

        return string.Empty;
    }

    private static bool HasLabel(JsonElement message, string label)
    {
        if (!message.TryGetProperty("labelIds", out JsonElement labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in labels.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), label, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Header(JsonElement message, string name)
    {
        JsonElement headersOwner = message;
        if (message.TryGetProperty("payload", out JsonElement payload))
        {
            headersOwner = payload;
        }

        if (!headersOwner.TryGetProperty("headers", out JsonElement headers) || headers.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string? found = null;
        foreach (JsonElement header in headers.EnumerateArray())
        {
            if (string.Equals(ReadString(header, "name"), name, StringComparison.OrdinalIgnoreCase))
            {
                if (found is not null)
                {
                    throw new MailClientException("Google returned a message with ambiguous headers.");
                }

                found = ReadString(header, "value") ?? string.Empty;
            }
        }

        return found ?? string.Empty;
    }

    private static JsonArray ReadAddressList(string header)
    {
        var addresses = new JsonArray();
        if (string.IsNullOrWhiteSpace(header))
        {
            return addresses;
        }

        if (header.Any(char.IsControl) || header.TrimEnd().EndsWith(','))
        {
            throw new MailClientException("Google returned a mail address that could not be read.");
        }

        try
        {
            foreach (string mailbox in SplitMailboxes(header))
            {
                MailAddress address = new(mailbox);
                if (!IsValidRfc5322Address(address.Address))
                {
                    throw new FormatException("Invalid mailbox address.");
                }

                addresses.Add((JsonNode)JsonValue.Create(address.Address)!);
            }
        }
        catch (FormatException)
        {
            throw new MailClientException("Google returned a mail address that could not be read.");
        }

        return addresses;
    }

    private static IEnumerable<string> SplitMailboxes(string header)
    {
        bool quoted = false;
        bool escaped = false;
        int angleDepth = 0;
        int start = 0;
        for (int i = 0; i < header.Length; i++)
        {
            char character = header[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quoted && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && character == '<') angleDepth++;
            if (!quoted && character == '>') angleDepth--;
            if (!quoted && angleDepth == 0 && character is ':' or ';')
            {
                throw new FormatException("Mailbox groups are not accepted.");
            }

            if (!quoted && angleDepth == 0 && character == ',')
            {
                string mailbox = header[start..i].Trim();
                if (mailbox.Length == 0) throw new FormatException("Empty mailbox.");
                yield return mailbox;
                start = i + 1;
            }
        }

        if (quoted || angleDepth != 0 || escaped)
        {
            throw new FormatException("Unclosed mailbox syntax.");
        }

        string last = header[start..].Trim();
        if (last.Length == 0) throw new FormatException("Empty mailbox.");
        yield return last;
    }

    private static bool IsValidRfc5322Address(string address)
        => address.Length is > 0 and <= 254 && !address.Any(char.IsControl);

    private static string ExtractAddress(string value)
    {
        int open = value.IndexOf('<', StringComparison.Ordinal);
        int close = value.IndexOf('>', StringComparison.Ordinal);
        string candidate = open >= 0 && close > open ? value[(open + 1)..close] : value;
        return candidate.Trim();
    }

    private static string SanitizeSearch(string search)
    {
        var builder = new StringBuilder(search.Length);
        foreach (char character in search.Trim())
        {
            if (!char.IsControl(character))
            {
                _ = builder.Append(character);
            }
        }

        string trimmed = builder.ToString().Trim();
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static string RequireId(string value, string name)
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
                _ = builder.Append(character);
            }
        }

        leaf = builder.ToString().Trim();
        if (leaf.Length == 0 || leaf is "." or "..")
        {
            return "attachment";
        }

        return leaf.Length > 180 ? leaf[..180] : leaf;
    }

    private static JsonObject AttachmentResult(string fileName, string contentType, string contentId, byte[] bytes)
        => new()
        {
            ["fileName"] = fileName,
            ["contentType"] = contentType,
            ["sizeBytes"] = bytes.Length,
            ["contentId"] = contentId,
            ["base64"] = Convert.ToBase64String(bytes),
        };

    private static string PublicPartId(JsonElement part, int ordinal)
    {
        string? partId = ReadString(part, "partId");
        string key = string.IsNullOrWhiteSpace(partId)
            ? "ordinal-" + ordinal.ToString(CultureInfo.InvariantCulture)
            : partId;
        return "part-" + key;
    }

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        int maxResponseBytes,
        string? tooLargeMessage,
        CancellationToken cancellationToken)
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
                if (total > maxResponseBytes)
                {
                    throw new MailClientException(tooLargeMessage ?? "Google returned a message that is too large.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
    }

    private static string ReadGmailError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                string? message = ReadString(error, "message");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return TrimMessage(message);
                }
            }
        }
        catch (JsonException)
        {
            return "Google rejected the mail request.";
        }

        return "Google rejected the mail request.";
    }

    private static string TrimMessage(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length > 300)
        {
            trimmed = trimmed[..300];
        }

        if (trimmed.Contains("eyJ", StringComparison.Ordinal)
            || trimmed.Contains("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return "Google rejected the mail request.";
        }

        return trimmed;
    }

    private static byte[] DecodeBase64Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        int remainder = padded.Length % 4;
        if (remainder == 2)
        {
            padded += "==";
        }
        else if (remainder == 3)
        {
            padded += "=";
        }

        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static long ReadNodeInt64(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out long number))
        {
            return number;
        }

        if (node is JsonValue integer && integer.TryGetValue(out int small))
        {
            return small;
        }

        return 0;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record ParsedBody(string? Plain, string? Html, List<GmailAttachment> Attachments, bool HasAttachments);

    private sealed record GmailAttachment(
        string Id,
        string Name,
        string ContentType,
        string? ContentId,
        long Size,
        string? GmailAttachmentId = null,
        byte[]? Inline = null);

    private static string? CleanContentId(string? contentId)
        => string.IsNullOrWhiteSpace(contentId) ? null : contentId.Trim().Trim('<', '>');

    private static string FileNameFor(string? rawName, string mime)
    {
        string safe = SafeFileName(rawName);
        if (!string.Equals(safe, "attachment", StringComparison.Ordinal))
        {
            return safe;
        }

        if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "attachment.pdf";
        }

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mime.Length > "image/".Length)
        {
            return "attachment." + mime["image/".Length..].Trim();
        }

        return safe;
    }

    private static bool IsSeparateFile(string mime)
        => !mime.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
            && !mime.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
            && !mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

    private static bool IsAttachmentDisposition(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        string token = header.Split(';')[0].Trim();
        return token.Equals("attachment", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FileNameFromDisposition(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        int encoded = header.IndexOf("filename*", StringComparison.OrdinalIgnoreCase);
        if (encoded >= 0)
        {
            string value = HeaderParameter(header, encoded);
            int separator = value.IndexOf("''", StringComparison.Ordinal);
            if (separator >= 0)
            {
                value = value[(separator + 2)..];
            }

            return Uri.UnescapeDataString(value.Trim());
        }

        int plain = header.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
        if (plain < 0)
        {
            return null;
        }

        string name = HeaderParameter(header, plain);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string HeaderParameter(string header, int keyStart)
    {
        int equals = header.IndexOf('=', keyStart);
        if (equals < 0 || equals + 1 >= header.Length)
        {
            return string.Empty;
        }

        string rest = header[(equals + 1)..].Trim();
        if (rest.StartsWith('"'))
        {
            int end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : string.Empty;
        }

        int semicolon = rest.IndexOf(';');
        return (semicolon >= 0 ? rest[..semicolon] : rest).Trim();
    }
}
