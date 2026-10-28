using System.Net;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.GoogleMail;

namespace Kautuka.IntegrationTests;

public sealed class GoogleMailConnectorTests
{
    private const string googleClientId = "123456789012-abcdef.apps.googleusercontent.com";
    private const string googleClientSecret = "google-client-secret-value";

    [Fact]
    public async Task MissingAccessTokenDoesNotCallGoogle()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Google was called."),
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"search":"invoice"}""");

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_list",
            arguments.RootElement,
            new Dictionary<string, string>(StringComparer.Ordinal),
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsError);
        Assert.Contains("Sign in with Google", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ListWithoutSearchReadsTheInboxAndDoesNotOpenAView()
    {
        var urls = new List<string>();
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                urls.Add(url);
                if (url.Contains("labelIds=INBOX", StringComparison.Ordinal))
                {
                    return JsonResponse("""{"messages":[{"id":"msg-1"}]}""");
                }

                return JsonResponse(Metadata("msg-1", "Re: Example project update", "Example User <am.shukla@example.com>", "Thanks. See the attached sample document.", unread: true));
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"maxResults":5}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_list",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Null(result.StructuredJson);
        Assert.Contains("msg-1", result.Text, StringComparison.Ordinal);
        Assert.Contains("am.shukla@example.com", result.Text, StringComparison.Ordinal);
        Assert.Contains("Re: Example project update", result.Text, StringComparison.Ordinal);
        Assert.Contains(urls, static url => url.Contains("labelIds=INBOX", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, static url => url.Contains("q=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListSearchUsesTheGmailQuery()
    {
        var urls = new List<string>();
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                urls.Add(url);
                if (url.Contains("q=", StringComparison.Ordinal))
                {
                    return JsonResponse("""{"messages":[{"id":"msg-2"}]}""");
                }

                return JsonResponse(Metadata("msg-2", "Invoice", "billing@example.com", "Amount due", unread: false));
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"search":"from:billing@example.com invoice"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_list",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("msg-2", result.Text, StringComparison.Ordinal);
        Assert.Contains(urls, static url => url.Contains("q=from%3Abilling%40example.com%20invoice", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, static url => url.Contains("labelIds=INBOX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListReceivedAfterOnlyKeepsTheInboxScope()
    {
        string? requestedUrl = null;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                requestedUrl = request.RequestUri?.AbsoluteUri;
                return JsonResponse("""{"messages":[]}""");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"receivedAfter":"2026-10-01T00:00:00Z"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_list", arguments.RootElement, environment, handler, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.NotNull(requestedUrl);
        Assert.Contains("q=in%3Ainbox%20after%3A", requestedUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadStripsTheStylesheetAndListsAttachmentNamesWithoutBytes()
    {
        string html = "<html><head><style>p{color:red}</style></head><body><p>Hello</p></body></html>";
        string encodedHtml = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/attachments/", StringComparison.Ordinal))
                {
                    return JsonResponse("""{"size":5,"data":"aGVsbG8"}""");
                }

                return JsonResponse(
                    "{\"id\":\"msg-1\",\"snippet\":\"Hello\",\"labelIds\":[\"INBOX\"],\"internalDate\":\"1728200000000\",\"payload\":{\"mimeType\":\"multipart/mixed\",\"headers\":[{\"name\":\"Subject\",\"value\":\"Notes\"},{\"name\":\"From\",\"value\":\"Example User <am.shukla@example.com>\"},{\"name\":\"To\",\"value\":\"owner@example.com\"}],\"parts\":[{\"mimeType\":\"text/html\",\"body\":{\"data\":\""
                    + encodedHtml
                    + "\"}},{\"filename\":\"notes.txt\",\"mimeType\":\"text/plain\",\"body\":{\"attachmentId\":\"att-1\",\"size\":5}}]}}");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-1"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_read",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("notes.txt", result.Text, StringComparison.Ordinal);
        Assert.Contains("Hello", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("aGVsbG8", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.StructuredJson);
        Assert.Contains("notes.txt", result.StructuredJson, StringComparison.Ordinal);
        Assert.Contains("message.html", result.StructuredJson, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", result.StructuredJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadKeepsABillPdfWhenGmailOmitsTheAttachmentIdOrTheFileName()
    {
        string inlinePdf = Convert.ToBase64String("%PDF-1.4 bill"u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/attachments/", StringComparison.Ordinal))
                {
                    return JsonResponse("{\"size\":4,\"data\":\"JVBERg\"}");
                }

                return JsonResponse(
                    "{\"id\":\"msg-bill\",\"snippet\":\"Your bill\",\"labelIds\":[\"INBOX\"],\"internalDate\":\"1728200000000\",\"payload\":{\"mimeType\":\"multipart/mixed\",\"headers\":[{\"name\":\"Subject\",\"value\":\"Example utility bill\"},{\"name\":\"From\",\"value\":\"hello@example.com\"}],\"parts\":["
                    + "{\"mimeType\":\"text/plain\",\"body\":{\"data\":\"SGkgYmlsbA\"}},"
                    + "{\"mimeType\":\"application/pdf\",\"filename\":\"\",\"headers\":[{\"name\":\"Content-Disposition\",\"value\":\"attachment; filename=\\\"Origin-bill.pdf\\\"\"}],\"body\":{\"attachmentId\":\"att-bill\",\"size\":4}},"
                    + "{\"partId\":\"2\",\"mimeType\":\"application/pdf\",\"filename\":\"notes-from-data.pdf\",\"body\":{\"size\":11,\"data\":\""
                    + inlinePdf
                    + "\"}}]}}");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-bill"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_read",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Origin-bill.pdf", result.Text, StringComparison.Ordinal);
        Assert.Contains("notes-from-data.pdf", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(inlinePdf, result.Text, StringComparison.Ordinal);
        Assert.NotNull(result.StructuredJson);
        Assert.Contains("Origin-bill.pdf", result.StructuredJson, StringComparison.Ordinal);
        Assert.Contains("notes-from-data.pdf", result.StructuredJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDownloadsAPdfWhenGmailChangesTheAttachmentId()
    {
        int reads = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/attachments/", StringComparison.Ordinal))
                {
                    Assert.Contains("/attachments/att-rotated", path, StringComparison.Ordinal);
                    return JsonResponse("{\"size\":8,\"data\":\"JVBERi0xLjQ\"}");
                }

                reads++;
                string attachmentId = reads == 1 ? "att-1" : "att-rotated";
                return JsonResponse(
                    "{\"id\":\"msg-bill\",\"snippet\":\"Your bill\",\"labelIds\":[\"INBOX\"],\"internalDate\":\"1728200000000\",\"payload\":{\"mimeType\":\"multipart/mixed\",\"headers\":[{\"name\":\"Subject\",\"value\":\"Example utility bill\"},{\"name\":\"From\",\"value\":\"hello@example.com\"}],\"parts\":["
                    + "{\"mimeType\":\"text/plain\",\"body\":{\"data\":\"SGkgYmlsbA\"}},"
                    + "{\"partId\":\"1\",\"mimeType\":\"application/pdf\",\"filename\":\"origin-statement-2026-10-07.pdf\",\"body\":{\"attachmentId\":\""
                    + attachmentId
                    + "\",\"size\":8}}]}}");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-bill"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_read",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("origin-statement-2026-10-07.pdf", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("JVBERi0xLjQ", result.Text, StringComparison.Ordinal);
        Assert.NotNull(result.StructuredJson);
        Assert.Contains("origin-statement-2026-10-07.pdf", result.StructuredJson, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"part-1\"", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachmentUsesThePartNameWhenEveryDownloadIdChanges()
    {
        string? downloaded = null;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/attachments/", StringComparison.Ordinal))
                {
                    downloaded = path;
                    return JsonResponse("{\"size\":8,\"data\":\"JVBERi0xLjQ\"}");
                }

                return JsonResponse(
                    "{\"id\":\"msg-bill\",\"payload\":{\"mimeType\":\"multipart/mixed\",\"parts\":["
                    + "{\"partId\":\"1\",\"mimeType\":\"text/plain\",\"filename\":\"notes.txt\",\"body\":{\"attachmentId\":\"notes-new\",\"size\":4}},"
                    + "{\"partId\":\"2\",\"mimeType\":\"application/pdf\",\"filename\":\"origin-statement.pdf\",\"body\":{\"attachmentId\":\"bill-new\",\"size\":8}}"
                    + "]}}");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-bill","attachmentId":"part-2"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_read_attachment",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("origin-statement.pdf", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", result.Text, StringComparison.Ordinal);
        Assert.Contains("/attachments/bill-new", downloaded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachmentDoesNotBorrowAnotherFileNameForAStaleDownloadId()
    {
        string? downloaded = null;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/attachments/", StringComparison.Ordinal))
                {
                    downloaded = path;
                    return JsonResponse("{\"size\":4,\"data\":\"bm90ZXM\"}");
                }

                return JsonResponse(
                    "{\"id\":\"msg-bill\",\"payload\":{\"mimeType\":\"multipart/mixed\",\"parts\":["
                    + "{\"partId\":\"1\",\"mimeType\":\"text/plain\",\"filename\":\"notes.txt\",\"body\":{\"attachmentId\":\"notes-new\",\"size\":4}},"
                    + "{\"partId\":\"2\",\"mimeType\":\"application/pdf\",\"filename\":\"origin-statement.pdf\",\"body\":{\"attachmentId\":\"bill-new\",\"size\":8}}"
                    + "]}}");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-bill","attachmentId":"bill-old"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_read_attachment",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Attachment attachment.", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("origin-statement.pdf", result.Text, StringComparison.Ordinal);
        Assert.Contains("/attachments/bill-old", downloaded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachmentAcceptsAFileLargerThanTheMessageCap()
    {
        byte[] raw = new byte[800_000];
        raw.AsSpan().Fill((byte)'A');
        string encoded = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(Encoding.UTF8.GetByteCount("{\"size\":800000,\"data\":\"" + encoded + "\"}") > 1_000_000);
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/attachments/", StringComparison.Ordinal))
                {
                    return JsonResponse("{\"size\":800000,\"data\":\"" + encoded + "\"}");
                }

                return JsonResponse(
                    "{\"id\":\"msg-bill\",\"payload\":{\"mimeType\":\"multipart/mixed\",\"parts\":["
                    + "{\"partId\":\"1\",\"mimeType\":\"application/pdf\",\"filename\":\"large.pdf\",\"body\":{\"attachmentId\":\"att-large\",\"size\":800000}}"
                    + "]}}");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-bill","attachmentId":"part-1"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_read_attachment",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("large.pdf", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DraftAndSendReturnTheToolTextWithoutAView()
    {
        var handler = new ScriptedHandler
        {
            Respond = static request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/send", StringComparison.Ordinal))
                {
                    return JsonResponse("{}");
                }

                return JsonResponse("""{"id":"draft-1"}""");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"to":"owner@example.com","subject":"Hello","body":"See you there."}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access",
        };

        MailToolResult draft = await GmailToolExecutor.CallAsync(
            "gmail_create_draft",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(draft.IsError, draft.Text);
        Assert.Null(draft.StructuredJson);
        Assert.Contains("draft-1", draft.Text, StringComparison.Ordinal);

        MailToolResult sent = await GmailToolExecutor.CallAsync(
            "gmail_send",
            arguments.RootElement,
            environment,
            handler,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(sent.IsError, sent.Text);
        Assert.Null(sent.StructuredJson);
        Assert.Contains("\"sent\":true", sent.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"mimeType":"application/octet-stream","filename":"","headers":[{"name":"Content-Disposition","value":"attachment"}],"body":{}}""")]
    [InlineData("""{"mimeType":"message/rfc822","filename":"","body":{}}""")]
    [InlineData("""{"mimeType":"message/rfc822","filename":"","parts":[{"mimeType":"text/plain","body":{"data":"SGVsbG8="}}],"body":{}}""")]
    [InlineData("""{"mimeType":"application/octet-stream","filename":"","body":{}}""")]
    [InlineData("""{"mimeType":"text/plain","filename":"","headers":[{"name":"Content-ID","value":"<hidden>"}],"body":{"attachmentId":"att-1"}}""")]
    public async Task SendRejectsDraftWithHiddenAttachmentMetadata(string part)
    {
        var requests = new List<string>();
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                requests.Add($"{request.Method} {request.RequestUri?.AbsolutePath}");
                return JsonResponse(
                    """{"id":"draft-1","message":{"labelIds":["DRAFT"],"payload":{"mimeType":"multipart/mixed","headers":[{"name":"To","value":"owner@example.com"},{"name":"Subject","value":"Approved"}],"parts":[{"mimeType":"text/plain","body":{"data":"SGVsbG8="}},"""
                    + part
                    + """]}}}""");
            },
        };
        using var client = new GmailClient("access", handler);

        MailClientException error = await Assert.ThrowsAsync<MailClientException>(() => client.SendAsync(
            new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(requests, request => request.EndsWith("/drafts/send", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<a href=\"https://evil.example\">Hello</a>")]
    [InlineData("<div>Hello attacker text</div>")]
    [InlineData("<div style=\"display:none\">Hello</div>")]
    [InlineData("<script>sendToAttacker()</script>Hello")]
    public async Task SendRejectsActiveOrDifferentHtmlEvenWhenPlainBodyMatches(string html)
    {
        string encodedHtml = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        int sends = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/drafts/send", StringComparison.Ordinal) == true)
                {
                    sends++;
                    return JsonResponse("{}");
                }

                return JsonResponse(AlternativeDraftJson(("text/plain", "SGVsbG8="), ("text/html", encodedHtml)));
            },
        };
        using var client = new GmailClient("access", handler);

        await Assert.ThrowsAsync<MailClientException>(() => client.SendAsync(
            new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task SendChecksBothMatchingPlainAndSafeHtmlBodies()
    {
        string html = "<div dir=\"ltr\">Hello</div>";
        string encodedHtml = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        int sends = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/drafts/send", StringComparison.Ordinal) == true)
                {
                    sends++;
                    return JsonResponse("{}");
                }

                return JsonResponse(AlternativeDraftJson(("text/plain", "SGVsbG8="), ("text/html", encodedHtml)));
            },
        };
        using var client = new GmailClient("access", handler);

        await client.SendAsync(
            new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    public async Task SendRejectsDuplicateAlternativeBodyParts(string duplicatedMime)
    {
        int sends = 0;
        string parts = duplicatedMime == "text/plain"
            ? "[{\"mimeType\":\"text/plain\",\"body\":{\"data\":\"SGVsbG8=\"}},{\"mimeType\":\"text/plain\",\"body\":{\"data\":\"QXR0YWNr\"}}]"
            : "[{\"mimeType\":\"text/html\",\"body\":{\"data\":\"SGVsbG8=\"}},{\"mimeType\":\"text/html\",\"body\":{\"data\":\"QXR0YWNr\"}}]";
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/drafts/send", StringComparison.Ordinal) == true) sends++;
                return JsonResponse(
                    "{\"id\":\"draft-1\",\"message\":{\"labelIds\":[\"DRAFT\"],\"payload\":{\"mimeType\":\"multipart/alternative\",\"headers\":[{\"name\":\"To\",\"value\":\"owner@example.com\"},{\"name\":\"Subject\",\"value\":\"Approved\"}],\"parts\":" + parts + "}}}");
            },
        };
        using var client = new GmailClient("access", handler);

        await Assert.ThrowsAsync<MailClientException>(() => client.SendAsync(
            new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task SendParsesQuotedDisplayNameCommasAsMailboxes()
    {
        int sends = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/drafts/send", StringComparison.Ordinal) == true)
                {
                    sends++;
                    return JsonResponse("{}");
                }

                return JsonResponse(
                    """{"id":"draft-1","message":{"labelIds":["DRAFT"],"payload":{"mimeType":"multipart/mixed","headers":[{"name":"To","value":"\"Smith, Jane\" <owner@example.com>, \"Doe, John\" <other@example.com>"},{"name":"Subject","value":"Approved"}],"parts":[{"mimeType":"text/plain","body":{"data":"SGVsbG8="}}]}}}""");
            },
        };
        using var client = new GmailClient("access", handler);

        await client.SendAsync(
            new MailMessageDraft(["owner@example.com", "other@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task SendRejectsDraftWithUndisclosedBcc()
    {
        int sends = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/drafts/send", StringComparison.Ordinal) == true) sends++;
                return JsonResponse(
                    """{"id":"draft-1","message":{"labelIds":["DRAFT"],"payload":{"mimeType":"multipart/mixed","headers":[{"name":"To","value":"owner@example.com"},{"name":"Bcc","value":"hidden@example.com"},{"name":"Subject","value":"Approved"}],"parts":[{"mimeType":"text/plain","body":{"data":"SGVsbG8="}}]}}}""");
            },
        };
        using var client = new GmailClient("access", handler);

        await Assert.ThrowsAsync<MailClientException>(() => client.SendAsync(
            new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData("owner@example.com,")]
    [InlineData("owner@example.com, ,other@example.com")]
    public async Task SendRejectsMalformedRecipientList(string recipientHeader)
    {
        int sends = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/drafts/send", StringComparison.Ordinal) == true) sends++;
                return JsonResponse(
                    "{" + "\"id\":\"draft-1\",\"message\":{\"labelIds\":[\"DRAFT\"],\"payload\":{\"mimeType\":\"multipart/mixed\",\"headers\":[{\"name\":\"To\",\"value\":\""
                    + recipientHeader
                    + "\"},{\"name\":\"Subject\",\"value\":\"Approved\"}],\"parts\":[{\"mimeType\":\"text/plain\",\"body\":{\"data\":\"SGVsbG8=\"}}]}}}");
            },
        };
        using var client = new GmailClient("access", handler);

        await Assert.ThrowsAsync<MailClientException>(() => client.SendAsync(
            new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task SendRejectsDuplicateRecipientHeaders()
    {
        int sends = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/drafts/send", StringComparison.Ordinal) == true) sends++;
                return JsonResponse(
                    """{"id":"draft-1","message":{"labelIds":["DRAFT"],"payload":{"mimeType":"multipart/mixed","headers":[{"name":"To","value":"owner@example.com"},{"name":"To","value":"hidden@example.com"},{"name":"Subject","value":"Approved"}],"parts":[{"mimeType":"text/plain","body":{"data":"SGVsbG8="}}]}}}""");
            },
        };
        using var client = new GmailClient("access", handler);

        await Assert.ThrowsAsync<MailClientException>(() => client.SendAsync(
            new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello"),
            "draft-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData("\nowner@example.com")]
    [InlineData("owner@example.com\r")]
    [InlineData("owner@example.com\r\nBcc: hidden@example.com")]
    public async Task DraftRejectsControlCharactersAtEveryAddressBoundary(string address)
    {
        using JsonDocument arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { to = address, subject = "Approved", body = "Hello" }));
        var handler = new ScriptedHandler { Respond = static _ => throw new InvalidOperationException("Invalid address reached Gmail.") };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"] = "access" };

        MailToolResult result = await GmailToolExecutor.CallAsync(
            "gmail_create_draft",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.IsError);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void BeginWithoutTheClientSecretIsNotConfigured()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Google was called."),
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile(googleClientId, clientSecret: null)]),
            new FakeRefreshTokens(),
            client,
            new ConnectorOAuthSessionStore());

        ConnectorOAuthBeginResponse begin = service.Begin("owner", "kautuka-google-mail", "http://127.0.0.1:20229");

        Assert.Equal("not_configured", begin.Status);
        Assert.Null(begin.StartUrl);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("not configured", begin.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Entra", begin.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizeUrlAsksGoogleForARefreshToken()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Google was called."),
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile(googleClientId, googleClientSecret)]),
            new FakeRefreshTokens(),
            client,
            new ConnectorOAuthSessionStore());

        ConnectorOAuthBeginResponse begin = service.Begin("owner", "kautuka-google-mail", "http://127.0.0.1:20229");

        Assert.Equal("redirect", begin.Status);
        Assert.NotNull(begin.StartUrl);
        Assert.Contains("accounts.google.com", begin.StartUrl, StringComparison.Ordinal);
        Assert.Contains("access_type=offline", begin.StartUrl, StringComparison.Ordinal);
        Assert.Contains("prompt=consent", begin.StartUrl, StringComparison.Ordinal);
        Assert.Contains(
            "redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:20229/api/connectors/oauth/callback"),
            begin.StartUrl,
            StringComparison.Ordinal);
        Assert.DoesNotContain("auth.kautuka.io", begin.StartUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalSignInRejectsALanAddress()
    {
        using var client = new ConnectorAuthorizationClient(new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Google was called."),
        });
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile(googleClientId, googleClientSecret)]),
            new FakeRefreshTokens(),
            client,
            new ConnectorOAuthSessionStore());

        ConnectorOAuthBeginResponse begin = service.Begin("owner", "kautuka-google-mail", "http://192.168.1.20:20229");

        Assert.Equal("rejected", begin.Status);
        Assert.Null(begin.StartUrl);
    }

    [Fact]
    public void HostedSignInLeavesTheClientIdOnTheAuthHost()
    {
        using var client = new ConnectorAuthorizationClient(new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Google was called."),
        });
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([BrokerProfile()]),
            new FakeRefreshTokens(),
            client,
            new ConnectorOAuthSessionStore(),
            Hosted);

        ConnectorOAuthBeginResponse begin = service.Begin("owner", "kautuka-google-mail", "http://127.0.0.1:20229");

        Assert.Equal("redirect", begin.Status);
        Assert.StartsWith("https://auth.kautuka.io/start#", begin.StartUrl, StringComparison.Ordinal);
        Assert.Contains("connector=kautuka-google-mail", begin.StartUrl, StringComparison.Ordinal);
        Assert.Contains("challenge=", begin.StartUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("client_id", begin.StartUrl, StringComparison.Ordinal);
        Assert.DoesNotContain(googleClientId, begin.StartUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("accounts.google.com", begin.StartUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteSendsTheClientSecretAndDoesNotReturnTokens()
    {
        const string refreshToken = "refresh-token-value";
        const string accessToken = "access-token-value";
        const string verifier = "verifier-secret-value";
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                Assert.Contains("oauth2.googleapis.com/token", request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
                string body = request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                Assert.Contains("client_secret=" + googleClientSecret, body, StringComparison.Ordinal);
                Assert.Contains("code_verifier=" + verifier, body, StringComparison.Ordinal);
                return JsonResponse(
                    "{\"token_type\":\"Bearer\",\"expires_in\":3600,\"access_token\":\""
                    + accessToken
                    + "\",\"refresh_token\":\""
                    + refreshToken
                    + "\"}");
            },
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var credentials = new FakeRefreshTokens();
        var sessions = new ConnectorOAuthSessionStore();
        sessions.Save("state-1", new ConnectorOAuthSession
        {
            UserId = "owner",
            ConnectorId = "kautuka-google-mail",
            CodeVerifier = verifier,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile(googleClientId, googleClientSecret)]),
            credentials,
            client,
            sessions);

        ConnectorOAuthCompleteResponse complete = await service.CompleteAsync(
            "owner",
            "auth-code",
            "state-1",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("connected", complete.Status);
        Assert.Equal(refreshToken, credentials.SavedRefreshToken);
        string returned = JsonSerializer.Serialize(complete);
        Assert.DoesNotContain(refreshToken, returned, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, returned, StringComparison.Ordinal);
        Assert.DoesNotContain(googleClientSecret, returned, StringComparison.Ordinal);
        Assert.DoesNotContain(verifier, returned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokerExchangeKeepsTheClientSecretOffTheAppliance()
    {
        string? body = null;
        string? dpop = null;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                body = request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                dpop = request.Headers.GetValues("DPoP").Single();
                HttpResponseMessage response = JsonResponse(
                    "{\"token_type\":\"Bearer\",\"expires_in\":3600,\"access_token\":\"access-token-value\",\"refresh_token\":\"krt1.kid.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA\"}");
                response.Headers.TryAddWithoutValidation("DPoP-Nonce", "nonce-1");
                return response;
            },
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var credentials = new MemoryTokens();
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([BrokerProfile()]),
            credentials,
            client,
            new ConnectorOAuthSessionStore(),
            Hosted);
        ConnectorOAuthBeginResponse begin = service.Begin("owner", "kautuka-google-mail", "http://127.0.0.1:20229");
        string state = StateFrom(begin.StartUrl!);

        ConnectorOAuthCompleteResponse complete = await service.CompleteAsync(
            "owner",
            "auth-code",
            state,
            TestContext.Current.CancellationToken,
            "signed-broker-ticket").ConfigureAwait(true);

        Assert.Equal("connected", complete.Status);
        Assert.NotNull(body);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_id", body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", body, StringComparison.Ordinal);
        Assert.Contains("code=auth-code", body, StringComparison.Ordinal);
        Assert.Contains("broker_ticket=signed-broker-ticket", body, StringComparison.Ordinal);
        Assert.Contains("return_origin=http%3A%2F%2F127.0.0.1%3A20229", body, StringComparison.Ordinal);
        Assert.Contains("https://auth.kautuka.io/google-mail/token", handler.LastUrl, StringComparison.Ordinal);
        Assert.NotNull(dpop);
        using JsonDocument proof = JsonDocument.Parse(DecodeJwtPayload(dpop));
        Assert.Equal("https://auth.kautuka.io/google-mail/token", proof.RootElement.GetProperty("htu").GetString());
        Assert.DoesNotContain("\"d\"", DecodeJwtPart(dpop.Split('.')[0]), StringComparison.Ordinal);
        Assert.StartsWith("krt1.", credentials.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"], StringComparison.Ordinal);
        Assert.Contains("\"d\"", credentials.Values["KAUTUKA_GOOGLE_MAIL_DPOP_KEY"], StringComparison.Ordinal);
        Assert.Equal("nonce-1", credentials.Values["KAUTUKA_GOOGLE_MAIL_DPOP_NONCE"]);
        string returned = JsonSerializer.Serialize(complete);
        Assert.DoesNotContain("krt1.", returned, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token-value", returned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshRetriesWhenGoogleAsksForADpopNonce()
    {
        var proofs = new List<string>();
        int calls = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                calls++;
                proofs.Add(request.Headers.GetValues("DPoP").Single());
                string refreshBody = request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                Assert.Contains("refresh_token=krt1.kid.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA", refreshBody, StringComparison.Ordinal);
                if (calls == 1)
                {
                    HttpResponseMessage rejected = new(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"use_dpop_nonce\"}", Encoding.UTF8, "application/json"),
                    };
                    rejected.Headers.TryAddWithoutValidation("DPoP-Nonce", "nonce-2");
                    return rejected;
                }

                HttpResponseMessage accepted = JsonResponse("{\"token_type\":\"Bearer\",\"expires_in\":3600,\"access_token\":\"access-2\"}");
                accepted.Headers.TryAddWithoutValidation("DPoP-Nonce", "nonce-3");
                return accepted;
            },
        };
        var credentials = new MemoryTokens();
        credentials.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"] = "krt1.kid.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA";
        credentials.Values["KAUTUKA_GOOGLE_MAIL_DPOP_KEY"] = ConnectorDpop.CreatePrivateJwk();
        using var client = new ConnectorAuthorizationClient(handler);
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([BrokerProfile()]),
            credentials,
            client,
            new ConnectorOAuthSessionStore(),
            Hosted);

        Dictionary<string, string> prepared = await service.PrepareAsync(
            "kautuka-google-mail",
            "owner",
            new Dictionary<string, string>(StringComparer.Ordinal),
            exchangeAccessToken: true,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, calls);
        Assert.Equal("access-2", prepared["KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN"]);
        Assert.False(prepared.ContainsKey("KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"));
        Assert.False(prepared.ContainsKey("KAUTUKA_GOOGLE_MAIL_DPOP_KEY"));
        using JsonDocument first = JsonDocument.Parse(DecodeJwtPayload(proofs[0]));
        using JsonDocument second = JsonDocument.Parse(DecodeJwtPayload(proofs[1]));
        Assert.NotEqual(first.RootElement.GetProperty("jti").GetString(), second.RootElement.GetProperty("jti").GetString());
        Assert.Equal("nonce-2", second.RootElement.GetProperty("nonce").GetString());
        Assert.Equal("nonce-3", credentials.Values["KAUTUKA_GOOGLE_MAIL_DPOP_NONCE"]);
    }

    [Fact]
    public async Task BeginRejectsExcessiveOAuthAttemptsForOneUser()
    {
        using var client = new ConnectorAuthorizationClient(new ScriptedHandler { Respond = _ => JsonResponse("{}") });
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([BrokerProfile()]),
            new MemoryTokens(),
            client,
            new ConnectorOAuthSessionStore(),
            Hosted);

        ConnectorOAuthBeginResponse[] attempts = Enumerable.Range(0, 11)
            .Select(_ => service.Begin("owner", "kautuka-google-mail", "http://127.0.0.1:20229"))
            .ToArray();

        Assert.All(attempts.Take(10), attempt => Assert.Equal("redirect", attempt.Status));
        Assert.Equal("rejected", attempts[10].Status);
        Assert.Null(attempts[10].StartUrl);
    }

    [Fact]
    public async Task LegacyHostedRefreshCredentialRequiresReconnectAndIsNotSentToBroker()
    {
        const string legacyToken = "old-plaintext-refresh-token";
        var handler = new ScriptedHandler { Respond = static _ => throw new InvalidOperationException("Legacy credential was sent to the broker.") };
        var credentials = new MemoryTokens();
        credentials.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"] = legacyToken;
        using var client = new ConnectorAuthorizationClient(handler);
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([BrokerProfile()]),
            credentials,
            client,
            new ConnectorOAuthSessionStore(),
            Hosted);

        ConnectorOAuthCatalogResponse status = await service.ListStatusAsync("owner", CancellationToken.None).ConfigureAwait(true);
        Assert.False(status.Connectors.Single().Connected);
        Assert.Contains("Reconnect", status.Connectors.Single().Message, StringComparison.Ordinal);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
            "kautuka-google-mail",
            "owner",
            new Dictionary<string, string>(StringComparer.Ordinal),
            exchangeAccessToken: true,
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("needs to be renewed", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(legacyToken, credentials.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"]);
    }

    [Fact]
    public async Task DisconnectRevokesTheBrokerKeyBeforeDeletingIt()
    {
        string privateJwk = ConnectorDpop.CreatePrivateJwk();
        string jkt = ConnectorDpop.Thumbprint(privateJwk);
        var proofs = new List<string>();
        int revokeCalls = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                proofs.Add(request.Headers.GetValues("DPoP").Single());
                revokeCalls++;
                Assert.Equal(HttpMethod.Delete, request.Method);
                string form = request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                Assert.Contains("refresh_token=krt1.kid.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA", form, StringComparison.Ordinal);
                if (revokeCalls == 1)
                {
                    var rejected = new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"use_dpop_nonce\"}", Encoding.UTF8, "application/json"),
                    };
                    rejected.Headers.TryAddWithoutValidation("DPoP-Nonce", "nonce-revoke");
                    return rejected;
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
        };
        var credentials = new MemoryTokens();
        credentials.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"] = "krt1.kid.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA";
        credentials.Values["KAUTUKA_GOOGLE_MAIL_DPOP_KEY"] = privateJwk;
        credentials.Values["KAUTUKA_GOOGLE_MAIL_DPOP_NONCE"] = "nonce-1";
        using var client = new ConnectorAuthorizationClient(handler);
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([BrokerProfile()]),
            credentials,
            client,
            new ConnectorOAuthSessionStore(),
            Hosted);

        ConnectorOAuthDisconnectResponse disconnect = await service.DisconnectAsync(
            "owner",
            "kautuka-google-mail",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(disconnect.Disconnected);
        Assert.Equal("https://auth.kautuka.io/google-mail/keys/" + jkt, handler.LastUrl);
        Assert.Equal(2, revokeCalls);
        using JsonDocument proof = JsonDocument.Parse(DecodeJwtPayload(proofs[0]));
        using JsonDocument retryProof = JsonDocument.Parse(DecodeJwtPayload(proofs[1]));
        Assert.Equal("DELETE", proof.RootElement.GetProperty("htm").GetString());
        Assert.Equal(handler.LastUrl, proof.RootElement.GetProperty("htu").GetString());
        Assert.Equal("nonce-revoke", retryProof.RootElement.GetProperty("nonce").GetString());
        Assert.NotEqual(proof.RootElement.GetProperty("jti").GetString(), retryProof.RootElement.GetProperty("jti").GetString());
        Assert.False(credentials.Values.ContainsKey("KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"));
        Assert.False(credentials.Values.ContainsKey("KAUTUKA_GOOGLE_MAIL_DPOP_KEY"));
        Assert.False(credentials.Values.ContainsKey("KAUTUKA_GOOGLE_MAIL_DPOP_NONCE"));
    }

    [Fact]
    public async Task DisconnectKeepsTheSignInWhenTheBrokerRejectsRevocation()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        var credentials = new MemoryTokens();
        credentials.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"] = "krt1.kid.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA";
        credentials.Values["KAUTUKA_GOOGLE_MAIL_DPOP_KEY"] = ConnectorDpop.CreatePrivateJwk();
        using var client = new ConnectorAuthorizationClient(handler);
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([BrokerProfile()]),
            credentials,
            client,
            new ConnectorOAuthSessionStore(),
            Hosted);

        ConnectorOAuthDisconnectResponse disconnect = await service.DisconnectAsync(
            "owner",
            "kautuka-google-mail",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(disconnect.Disconnected);
        Assert.StartsWith("krt1.", credentials.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"], StringComparison.Ordinal);
        Assert.True(credentials.Values.ContainsKey("KAUTUKA_GOOGLE_MAIL_DPOP_KEY"));
    }

    [Theory]
    [InlineData("not-configured", null)]
    [InlineData("not-a-guid", null)]
    [InlineData("123456789012-abcdef.apps.googleusercontent.com", "123456789012-abcdef.apps.googleusercontent.com")]
    public void ReadClientIdAcceptsAGoogleClientId(string? configured, string? expected)
    {
        Assert.Equal(expected, ConnectorAuthProfile.ReadClientId(configured));
    }

    private static string Metadata(string id, string subject, string from, string snippet, bool unread)
    {
        string labels = unread ? "[\"INBOX\",\"UNREAD\"]" : "[\"INBOX\"]";
        return "{\"id\":\"" + id
            + "\",\"snippet\":\"" + snippet
            + "\",\"labelIds\":" + labels
            + ",\"internalDate\":\"1728200000000\",\"payload\":{\"headers\":[{\"name\":\"Subject\",\"value\":\""
            + subject
            + "\"},{\"name\":\"From\",\"value\":\""
            + from
            + "\"},{\"name\":\"Date\",\"value\":\"Mon, 6 Oct 2026 08:29:00 +0000\"}]}}";
    }

    private static ConnectorAuthProfile Profile(string? clientId, string? clientSecret = null) => new()
    {
        Id = "kautuka-google-mail",
        Name = "Google mail",
        Description = "Read, draft, and send mail.",
        Grant = "authorization_code",
        AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenUrl = "https://oauth2.googleapis.com/token",
        Scopes = "https://www.googleapis.com/auth/gmail.modify",
        RedirectUri = "https://auth.kautuka.io/callback",
        StartUrl = "https://auth.kautuka.io/start",
        RefreshCredentialKey = "KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN",
        AccessTokenEnvironmentKey = "KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN",
        RevokeUrl = "https://myaccount.google.com/permissions",
        SignInLabel = "Sign in with Google",
        EntryPoint = "kautuka-google-mail",
        ToolNames = ["gmail_list", "gmail_read", "gmail_read_attachment", "gmail_create_draft", "gmail_update_draft", "gmail_send"],
        AskFirst = ["gmail_send"],
        ClientId = clientId,
        ClientSecret = clientSecret,
        RequiresClientSecret = true,
        Prompt = "consent",
        AccessType = "offline",
        ConfigurationMessage = "Google mail sign-in is not configured on this appliance yet.",
    };

    private static ConnectorAuthProfile BrokerProfile() => new()
    {
        Id = "kautuka-google-mail",
        Name = "Google mail",
        Description = "Read, draft, and send mail.",
        Grant = "authorization_code",
        AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenUrl = "https://oauth2.googleapis.com/token",
        Scopes = "https://www.googleapis.com/auth/gmail.modify",
        RedirectUri = "https://auth.kautuka.io/callback",
        StartUrl = "https://auth.kautuka.io/start",
        RefreshCredentialKey = "KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN",
        AccessTokenEnvironmentKey = "KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN",
        RevokeUrl = "https://myaccount.google.com/permissions",
        SignInLabel = "Sign in with Google",
        EntryPoint = "kautuka-google-mail",
        ToolNames = ["gmail_list", "gmail_read", "gmail_read_attachment", "gmail_create_draft", "gmail_update_draft", "gmail_send"],
        AskFirst = ["gmail_send"],
        ClientId = googleClientId,
        RequiresClientSecret = true,
        UseDpop = true,
        DpopKeyCredentialKey = "KAUTUKA_GOOGLE_MAIL_DPOP_KEY",
        DpopNonceCredentialKey = "KAUTUKA_GOOGLE_MAIL_DPOP_NONCE",
        Prompt = "consent",
        AccessType = "offline",
        ConfigurationMessage = "Google mail sign-in is not configured on this appliance yet.",
    };

    private static ConnectorAuthEndpoint Hosted { get; } = ConnectorAuthEndpoint.Parse("https://auth.kautuka.io");

    private static string StateFrom(string startUrl)
    {
        int hash = startUrl.IndexOf('#', StringComparison.Ordinal);
        string fragment = hash >= 0 ? startUrl[(hash + 1)..] : startUrl;
        foreach (string pair in fragment.Split('&'))
        {
            if (pair.StartsWith("state=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair["state=".Length..]);
            }
        }

        string query = new Uri(startUrl).Query.TrimStart('?');
        foreach (string pair in query.Split('&'))
        {
            if (pair.StartsWith("state=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair["state=".Length..]);
            }
        }

        throw new InvalidOperationException("The authorize URL has no state.");
    }

    private static string DecodeJwtPayload(string jwt)
        => DecodeJwtPart(jwt.Split('.')[1]);

    private static string DecodeJwtPart(string part)
    {
        string padded = part.Replace('-', '+').Replace('_', '/');
        int remainder = padded.Length % 4;
        if (remainder == 2)
        {
            padded += "==";
        }
        else if (remainder == 3)
        {
            padded += "=";
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class FakeRefreshTokens : IConnectorRefreshTokenStore
    {
        public string? SavedRefreshToken { get; private set; }

        public Task<string?> GetAsync(string userId, string key, CancellationToken cancellationToken)
            => Task.FromResult(SavedRefreshToken);

        public Task SetAsync(string userId, string key, string refreshToken, CancellationToken cancellationToken)
        {
            SavedRefreshToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string userId, string key, CancellationToken cancellationToken)
        {
            SavedRefreshToken = null;
            return Task.CompletedTask;
        }
    }

    private static string AlternativeDraftJson(params (string MimeType, string Data)[] parts)
    {
        return JsonSerializer.Serialize(new
        {
            id = "draft-1",
            message = new
            {
                labelIds = new[] { "DRAFT" },
                payload = new
                {
                    mimeType = "multipart/alternative",
                    headers = new[]
                    {
                        new { name = "To", value = "owner@example.com" },
                        new { name = "Subject", value = "Approved" },
                    },
                    parts = parts.Select(part => new
                    {
                        mimeType = part.MimeType,
                        body = new { data = part.Data },
                    }),
                },
            },
        });
    }

    private sealed class MemoryTokens : IConnectorRefreshTokenStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string userId, string key, CancellationToken cancellationToken)
            => Task.FromResult(Values.TryGetValue(key, out string? value) ? value : null);

        public Task SetAsync(string userId, string key, string refreshToken, CancellationToken cancellationToken)
        {
            Values[key] = refreshToken;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string userId, string key, CancellationToken cancellationToken)
        {
            _ = Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastUrl { get; private set; }

        public required Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(Respond(request));
        }
    }
}
