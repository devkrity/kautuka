using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kautuka.GoogleMail;

internal static class MailHtmlText
{
    private static readonly HashSet<string> approvedDraftTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "blockquote", "body", "br", "code", "div", "em", "h1", "h2", "h3", "h4", "h5", "h6",
        "hr", "i", "li", "ol", "p", "pre", "span", "strike", "strong", "s", "table", "tbody",
        "td", "th", "thead", "tfoot", "tr", "u", "ul",
    };

    private static readonly HashSet<string> voidDraftTags = new(StringComparer.OrdinalIgnoreCase) { "br", "hr" };

    private static readonly Regex allowedDirection = new(
        @"^\s+dir\s*=\s*(?:""(?:ltr|rtl|auto)""|'(?:ltr|rtl|auto)'|(?:ltr|rtl|auto))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSafeForDraftApproval(string html)
    {
        var openTags = new Stack<string>();
        int offset = 0;
        while (offset < html.Length)
        {
            int start = html.IndexOf('<', offset);
            if (start < 0)
            {
                break;
            }

            int end = html.IndexOf('>', start + 1);
            if (end < 0 || html.IndexOf('<', start + 1, end - start - 1) >= 0)
            {
                return false;
            }

            ReadOnlySpan<char> source = html.AsSpan(start, end - start + 1);
            if (!TryReadDraftTag(source, out string? name, out bool closing, out bool selfClosing, out string attributes)
                || !approvedDraftTags.Contains(name))
            {
                return false;
            }

            bool isVoid = voidDraftTags.Contains(name);
            if (closing)
            {
                if (selfClosing || attributes.Length != 0 || openTags.Count == 0
                    || !string.Equals(openTags.Pop(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else
            {
                if (attributes.Length > 0 && !allowedDirection.IsMatch(attributes))
                {
                    return false;
                }

                if (isVoid)
                {
                    if (selfClosing)
                    {
                        offset = end + 1;
                        continue;
                    }
                }
                else
                {
                    if (selfClosing)
                    {
                        return false;
                    }

                    openTags.Push(name);
                }
            }

            offset = end + 1;
        }

        return openTags.Count == 0;
    }

    public static string ToPlain(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        string visible = Regex.Replace(html, "<!--[\\s\\S]*?-->", " ", RegexOptions.CultureInvariant);
        visible = Regex.Replace(visible, "<head\\b[\\s\\S]*?</head>", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        visible = Regex.Replace(visible, "<(script|style)\\b[\\s\\S]*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string withBreaks = Regex.Replace(visible, "<\\s*br\\s*/?>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        withBreaks = Regex.Replace(withBreaks, "</\\s*(?:p|div|tr|li|h[1-6])\\s*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string stripped = Regex.Replace(withBreaks, "<[^>]+>", string.Empty, RegexOptions.CultureInvariant);
        stripped = stripped
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase);
        stripped = Regex.Replace(stripped, "[\\t ]+", " ");
        stripped = Regex.Replace(stripped, "(?:\\r?\\n[\\t ]*){3,}", "\n\n");
        return stripped.Trim();
    }

    private static bool TryReadDraftTag(
        ReadOnlySpan<char> source,
        out string name,
        out bool closing,
        out bool selfClosing,
        out string attributes)
    {
        name = string.Empty;
        closing = false;
        selfClosing = false;
        attributes = string.Empty;
        if (source.Length < 3 || source[0] != '<' || source[^1] != '>')
        {
            return false;
        }

        ReadOnlySpan<char> inner = source[1..^1];
        if (inner[0] == '/')
        {
            closing = true;
            inner = inner[1..];
        }

        int index = 0;
        while (index < inner.Length && char.IsAsciiLetterOrDigit(inner[index]))
        {
            index++;
        }

        if (index == 0 || !char.IsAsciiLetter(inner[0]))
        {
            return false;
        }

        name = inner[..index].ToString();
        string tail = inner[index..].ToString();
        string trimmedTail = tail.TrimEnd();
        if (trimmedTail.EndsWith('/'))
        {
            selfClosing = true;
            trimmedTail = trimmedTail[..^1];
        }

        if (closing)
        {
            return trimmedTail.Length == 0;
        }

        attributes = trimmedTail;
        return attributes.Length == 0 || char.IsWhiteSpace(attributes[0]);
    }
}

/// <summary>
/// Builds the opened-message view. File bytes travel beside the view and are stored by the host.
/// List, draft, and sent results stay in the tool text.
/// </summary>
internal static class MailView
{
    public static MailToolResult FromList(JsonArray messages)
    {
        var lines = new JsonArray();
        foreach (JsonNode? node in messages)
        {
            if (node is not JsonObject message)
            {
                continue;
            }

            lines.Add(message.DeepClone());
        }

        return new MailToolResult(false, new JsonObject { ["messages"] = lines }.ToJsonString());
    }

    public static MailToolResult FromRead(JsonObject summary, IReadOnlyList<DownloadedMailFile> files)
    {
        string subject = summary["subject"]?.GetValue<string>() ?? "(no subject)";
        string from = summary["from"]?.GetValue<string>() ?? string.Empty;
        string plain = summary["body"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(plain))
        {
            plain = summary["preview"]?.GetValue<string>() ?? string.Empty;
        }
        if (plain.Length > 4000)
        {
            plain = plain[..4000];
        }

        string html = summary["html"]?.GetValue<string>() ?? string.Empty;
        var fileNodes = new JsonArray();
        var fields = new JsonArray();
        AddField(fields, "From", from);
        AddField(fields, "To", JoinValues(summary["to"]));
        AddField(fields, "Cc", JoinValues(summary["cc"]));
        AddField(fields, "When", FormatWhen(summary["received"]?.GetValue<string>()));
        var blocks = new JsonArray
        {
            (JsonNode)Record(null, fields, plain),
        };

        if (!string.IsNullOrEmpty(html))
        {
            foreach (DownloadedMailFile file in files)
            {
                if (!string.IsNullOrWhiteSpace(file.ContentId) && file.Bytes.Length <= 256 * 1024)
                {
                    html = EmbedContentId(html, file.ContentId, file.ContentType, file.Bytes);
                }
            }

            AddFile(blocks, fileNodes, "message-html", "message.html", "text/html", Encoding.UTF8.GetBytes(html), "source");
        }

        foreach (DownloadedMailFile file in files)
        {
            AddFile(blocks, fileNodes, file.FileId, file.Name, file.ContentType, file.Bytes, "primary");
        }

        blocks.Add((JsonNode)new JsonObject
        {
            ["type"] = "actions",
            ["items"] = new JsonArray
            {
                (JsonNode)new JsonObject
                {
                    ["name"] = "reply",
                    ["label"] = "Reply",
                },
            },
        });

        JsonObject view = Create(subject, blocks);
        var structured = new JsonObject
        {
            ["view"] = view,
            ["files"] = fileNodes,
        };
        var attachments = new JsonArray();
        foreach (DownloadedMailFile file in files)
        {
            attachments.Add((JsonNode)new JsonObject
            {
                ["id"] = file.FileId,
                ["name"] = file.Name,
                ["contentType"] = file.ContentType,
                ["sizeBytes"] = file.Bytes.LongLength,
            });
        }

        if (summary["attachments"] is JsonArray source)
        {
            foreach (JsonNode? node in source)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                string? error = item["downloadError"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(error))
                {
                    continue;
                }

                attachments.Add((JsonNode)new JsonObject
                {
                    ["id"] = item["id"]?.GetValue<string>() ?? string.Empty,
                    ["name"] = item["name"]?.GetValue<string>() ?? "attachment",
                    ["error"] = error,
                });
            }
        }

        var model = new JsonObject
        {
            ["id"] = summary["id"]?.GetValue<string>() ?? string.Empty,
            ["subject"] = subject,
            ["from"] = from,
            ["preview"] = summary["preview"]?.GetValue<string>() ?? string.Empty,
            ["body"] = plain,
            ["attachments"] = attachments,
        };
        return new MailToolResult(false, model.ToJsonString(), structured.ToJsonString());
    }

    public static MailToolResult FromAttachment(string name, string contentType, byte[] bytes)
    {
        var blocks = new JsonArray();
        var fileNodes = new JsonArray();
        AddFile(blocks, fileNodes, "attachment", name, contentType, bytes, "primary");
        JsonObject view = Create(name, blocks);
        var structured = new JsonObject
        {
            ["view"] = view,
            ["files"] = fileNodes,
        };
        return new MailToolResult(false, $"Attachment {name}.", structured.ToJsonString());
    }

    private static JsonObject Create(string title, JsonArray blocks)
        => new()
        {
            ["catalogVersion"] = 1,
            ["viewId"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title + blocks.ToJsonString())))[..12],
            ["title"] = title,
            ["blocks"] = blocks,
        };

    private static JsonObject Record(string? status, JsonArray fields, string? body)
    {
        var record = new JsonObject
        {
            ["type"] = "record",
            ["fields"] = fields,
        };
        if (!string.IsNullOrWhiteSpace(status))
        {
            record["status"] = status;
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            record["body"] = body;
        }

        return record;
    }

    private static void AddField(JsonArray fields, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        fields.Add((JsonNode)new JsonObject
        {
            ["label"] = label,
            ["value"] = value.Trim(),
        });
    }

    private static string JoinValues(JsonNode? node)
    {
        if (node is not JsonArray values)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (JsonNode? value in values)
        {
            string? text = value?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
        }

        return string.Join(", ", parts);
    }

    private static string FormatWhen(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset instant))
        {
            return string.Empty;
        }

        return instant.ToUniversalTime().ToString("d MMM, h:mm tt", CultureInfo.InvariantCulture);
    }

    private static void AddFile(JsonArray blocks, JsonArray fileNodes, string fileId, string name, string contentType, byte[] bytes, string role)
    {
        blocks.Add((JsonNode)new JsonObject
        {
            ["type"] = "file",
            ["fileId"] = fileId,
            ["name"] = name,
            ["mediaType"] = contentType,
            ["role"] = role,
        });
        fileNodes.Add((JsonNode)new JsonObject
        {
            ["fileId"] = fileId,
            ["name"] = name,
            ["mediaType"] = contentType,
            ["base64"] = Convert.ToBase64String(bytes),
        });
    }

    private static string EmbedContentId(string html, string contentId, string contentType, byte[] bytes)
    {
        string cid = contentId.Trim().Trim('<', '>');
        if (cid.Length == 0 || html.Length == 0)
        {
            return html;
        }

        string dataUri = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
        return html.Replace("cid:" + cid, dataUri, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record DownloadedMailFile(string FileId, string Name, string ContentType, string? ContentId, byte[] Bytes);
