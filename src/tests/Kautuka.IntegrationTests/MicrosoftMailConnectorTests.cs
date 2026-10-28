using System.Net;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.M365Mail;

namespace Kautuka.IntegrationTests;

public sealed class MicrosoftMailConnectorTests
{
    [Fact]
    public async Task BeginDoesNotCallMicrosoftWhenClientIdIsUnconfigured()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Microsoft was called."),
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile(clientId: null)]),
            new FakeRefreshTokens(),
            client,
            new ConnectorOAuthSessionStore());

        ConnectorOAuthBeginResponse begin = service.Begin("owner", "kautuka-m365-mail", "http://127.0.0.1:20229");

        Assert.Equal("not_configured", begin.Status);
        Assert.Null(begin.StartUrl);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("not configured", begin.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KAUTUKA_M365_CLIENT_ID", begin.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteStoresRefreshTokenAndDoesNotReturnIt()
    {
        const string refreshToken = "refresh-token-value";
        const string accessToken = "access-token-value";
        const string verifier = "verifier-secret-value";
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                Assert.Contains("/token", request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
                string body = request.Content!.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.Contains("redirect_uri=https%3A%2F%2Fauth.kautuka.io%2Fcallback", body, StringComparison.Ordinal);
                Assert.Contains("code_verifier=" + verifier, body, StringComparison.Ordinal);
                Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
                return JsonResponse(
                    $$"""
                    {"token_type":"Bearer","expires_in":3600,"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}"}
                    """);
            },
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var credentials = new FakeRefreshTokens();
        var sessions = new ConnectorOAuthSessionStore();
        sessions.Save("state-secret", new ConnectorOAuthSession
        {
            UserId = "owner",
            ConnectorId = "kautuka-m365-mail",
            CodeVerifier = verifier,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile("11111111-1111-1111-1111-111111111111")]),
            credentials,
            client,
            sessions);

        ConnectorOAuthCompleteResponse complete = await service.CompleteAsync("owner", "auth-code", "state-secret", CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("connected", complete.Status);
        Assert.Equal(refreshToken, credentials.SavedRefreshToken);
        string json = JsonSerializer.Serialize(complete);
        Assert.DoesNotContain(refreshToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(verifier, json, StringComparison.Ordinal);
        Assert.False(sessions.TryTake("state-secret", out _));
    }

    [Fact]
    public async Task HostedExchangeKeepsTheClientIdOnTheAuthHost()
    {
        string? body = null;
        string? dpop = null;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                body = request.Content!.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                dpop = request.Headers.GetValues("DPoP").Single();
                Assert.Contains("https://auth.kautuka.io/microsoft-mail/token", request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
                return JsonResponse(
                    """{"token_type":"Bearer","expires_in":3600,"access_token":"access-token-value","refresh_token":"krt1.kid.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA"}""");
            },
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var credentials = new FakeRefreshTokens();
        var sessions = new ConnectorOAuthSessionStore();
        var service = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile(clientId: null, hostedKey: true)]),
            credentials,
            client,
            sessions,
            ConnectorAuthEndpoint.Parse("https://auth.kautuka.io"));

        ConnectorOAuthBeginResponse begin = service.Begin("owner", "kautuka-m365-mail", "http://127.0.0.1:20229");
        Assert.Equal("redirect", begin.Status);
        Assert.DoesNotContain("client_id", begin.StartUrl, StringComparison.Ordinal);
        string state = StateFrom(begin.StartUrl!);

        ConnectorOAuthCompleteResponse complete = await service.CompleteAsync("owner", "auth-code", state, CancellationToken.None, "signed-broker-ticket").ConfigureAwait(true);

        Assert.Equal("connected", complete.Status);
        Assert.NotNull(body);
        Assert.DoesNotContain("client_id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", body, StringComparison.Ordinal);
        Assert.Contains("broker_ticket=signed-broker-ticket", body, StringComparison.Ordinal);
        Assert.Contains("return_origin=http%3A%2F%2F127.0.0.1%3A20229", body, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Fauth.kautuka.io%2Fcallback", body, StringComparison.Ordinal);
        Assert.StartsWith("krt1.", credentials.SavedRefreshToken, StringComparison.Ordinal);
        Assert.NotNull(dpop);
        using JsonDocument proof = JsonDocument.Parse(DecodeJwtPayload(dpop));
        Assert.Equal("https://auth.kautuka.io/microsoft-mail/token", proof.RootElement.GetProperty("htu").GetString());
        Assert.False(string.IsNullOrWhiteSpace(credentials.SavedDpopKey));
    }

    [Fact]
    public async Task BrokerStripsRefreshTokenAndSavesRotatedToken()
    {
        const string oldRefresh = "old-refresh-token";
        const string newRefresh = "new-refresh-token";
        const string accessToken = "short-lived-access-token";
        var handler = new ScriptedHandler
        {
            Respond = _ => JsonResponse(
                $$"""
                {"token_type":"Bearer","expires_in":3600,"access_token":"{{accessToken}}","refresh_token":"{{newRefresh}}"}
                """),
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var credentials = new FakeRefreshTokens();
        var broker = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile("11111111-1111-1111-1111-111111111111")]),
            credentials,
            client,
            new ConnectorOAuthSessionStore());
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_REFRESH_TOKEN"] = oldRefresh,
        };

        Dictionary<string, string> prepared = await broker.PrepareAsync(
            "user-1::kautuka-m365-mail",
            "user-1",
            environment,
            exchangeAccessToken: true,
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(prepared.ContainsKey("KAUTUKA_M365_REFRESH_TOKEN"));
        Assert.Equal(accessToken, prepared["KAUTUKA_M365_ACCESS_TOKEN"]);
        Assert.Equal(newRefresh, credentials.SavedRefreshToken);
    }

    [Fact]
    public async Task BrokerListDoesNotExchangeOrKeepRefreshToken()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Microsoft was called."),
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var broker = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile("11111111-1111-1111-1111-111111111111")]),
            new FakeRefreshTokens(),
            client,
            new ConnectorOAuthSessionStore());
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_REFRESH_TOKEN"] = "old-refresh-token",
        };

        Dictionary<string, string> prepared = await broker.PrepareAsync(
            "kautuka-m365-mail",
            "user-1",
            environment,
            exchangeAccessToken: false,
            CancellationToken.None).ConfigureAwait(true);

        Assert.Empty(prepared);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BrokerDoesNotCallMicrosoftWhenClientIdIsUnconfigured()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Microsoft was called."),
        };
        using var client = new ConnectorAuthorizationClient(handler);
        var broker = new ConnectorOAuthService(
            new ConnectorAuthCatalog([Profile(clientId: null)]),
            new FakeRefreshTokens(),
            client,
            new ConnectorOAuthSessionStore());
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_REFRESH_TOKEN"] = "old-refresh-token",
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.PrepareAsync(
            "kautuka-m365-mail",
            "user-1",
            environment,
            exchangeAccessToken: true,
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KAUTUKA_M365_CLIENT_ID", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
        Assert.False(environment.ContainsKey("KAUTUKA_M365_REFRESH_TOKEN"));
    }

    [Fact]
    public async Task SendRefusesDraftThatDoesNotMatchApproval()
    {
        var calls = new List<string>();
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                calls.Add($"{request.Method} {request.RequestUri?.AbsolutePath}");
                return JsonResponse(
                    """
                    {"id":"draft-1","subject":"Different","isDraft":true,"hasAttachments":false,"attachments":[],"bccRecipients":[],"body":{"contentType":"Text","content":"Hello"},"toRecipients":[{"emailAddress":{"address":"owner@example.com"}}],"ccRecipients":[]}
                    """);
            },
        };
        using JsonDocument arguments = JsonDocument.Parse(
            """
            {"draftId":"draft-1","to":["owner@example.com"],"subject":"Approved","body":"Hello"}
            """);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_send",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.IsError);
        Assert.Contains("does not match", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, call => call.Contains("/send", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"bccRecipients":[{"emailAddress":{"address":"hidden@example.com"}}],"hasAttachments":false,"attachments":[]}""")]
    [InlineData("""{"bccRecipients":[],"hasAttachments":true,"attachments":[]}""")]
    [InlineData("""{"bccRecipients":"unexpected","hasAttachments":false,"attachments":[]}""")]
    [InlineData("""{"bccRecipients":[],"hasAttachments":"false","attachments":[]}""")]
    [InlineData("""{"bccRecipients":[],"hasAttachments":false,"attachments":[{"id":"inline-image","isInline":true}]}""")]
    public async Task SendRejectsHiddenRecipientsAttachmentsAndUnconfirmedDraftMetadata(string safetyMetadata)
    {
        int sends = 0;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/send", StringComparison.Ordinal) == true)
                {
                    sends++;
                    return JsonResponse("{}");
                }

                return JsonResponse(
                    "{" + "\"id\":\"draft-1\",\"subject\":\"Approved\",\"isDraft\":true,\"body\":{\"contentType\":\"Text\",\"content\":\"Hello\"},\"toRecipients\":[{\"emailAddress\":{\"address\":\"owner@example.com\"}}],\"ccRecipients\":[],"
                            + safetyMetadata[1..^1]
                            + "}");
            },
        };
        using var client = new GraphMailClient("access", handler);
        var draft = new MailMessageDraft(["owner@example.com"], [], "Approved", "Hello");

        await Assert.ThrowsAsync<MailClientException>(() => client.SendAsync(draft, "draft-1", CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData("\nowner@example.com")]
    [InlineData("owner@example.com\r")]
    [InlineData("owner@example.com\r\nBcc: hidden@example.com")]
    public async Task DraftRejectsControlCharactersAtEveryAddressBoundary(string address)
    {
        using JsonDocument arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { to = address, subject = "Approved", body = "Hello" }));
        var handler = new ScriptedHandler { Respond = static _ => throw new InvalidOperationException("Invalid address reached Graph.") };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["KAUTUKA_M365_ACCESS_TOKEN"] = "access" };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_create_draft",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.IsError);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UpdateDraftOmitsRecipientsThatWereNotSupplied()
    {
        string? body = null;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                if (request.Content is not null)
                {
                    body = request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                return JsonResponse("""{"id":"draft-1","subject":"Updated"}""");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"draftId":"draft-1","subject":"Updated"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_update_draft",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(result.IsError);
        Assert.NotNull(body);
        Assert.Contains("Updated", body, StringComparison.Ordinal);
        Assert.DoesNotContain("toRecipients", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAccessTokenDoesNotCallGraph()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => throw new InvalidOperationException("Graph was called."),
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"search":"invoice"}""");

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_list",
            arguments.RootElement,
            new Dictionary<string, string>(StringComparer.Ordinal),
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.IsError);
        Assert.Contains("Sign in with Microsoft", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ListReturnsTheMessageSummaryWithoutAView()
    {
        var handler = new ScriptedHandler
        {
            Respond = static _ => JsonResponse(
                """
                {"value":[
                  {"id":"msg-1","subject":"Re: Example project update","from":{"emailAddress":{"address":"am.shukla@example.com"}},"receivedDateTime":"2026-10-06T08:29:00Z","isRead":false,"bodyPreview":"Thanks.\r\n\r\nSee the attached sample document."},
                  {"id":"msg-2","subject":"Invoice","from":{"emailAddress":{"address":"billing@example.com"}},"receivedDateTime":"2026-10-05T01:00:00Z","isRead":true,"bodyPreview":"Amount due"}
                ]}
                """),
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"maxResults":5}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_list",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Null(result.StructuredJson);
        Assert.Contains("msg-1", result.Text, StringComparison.Ordinal);
        Assert.Contains("am.shukla@example.com", result.Text, StringComparison.Ordinal);
        Assert.Contains("Re: Example project update", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"receivedAfter\":\"2026-10-01T00:00:00Z\"}", false)]
    [InlineData("{\"search\":\"invoice\"}", true)]
    [InlineData("{\"search\":\"invoice\",\"receivedAfter\":\"2026-10-01T00:00:00Z\"}", true)]
    public async Task ListUsesInboxOrderingOnlyWithoutSearch(string argumentsJson, bool hasSearch)
    {
        string? requestedUrl = null;
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                requestedUrl = request.RequestUri?.AbsoluteUri;
                return JsonResponse("""{"value":[]}""");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_list", arguments.RootElement, environment, handler, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.NotNull(requestedUrl);
        string url = Uri.UnescapeDataString(requestedUrl);
        Assert.Contains(hasSearch ? "/messages?" : "/mailFolders/inbox/messages?", url, StringComparison.Ordinal);
        if (hasSearch)
        {
            Assert.Contains("$search=", url, StringComparison.Ordinal);
            Assert.Contains("$top=15", url, StringComparison.Ordinal);
            Assert.Contains("$select=id,subject,from,receivedDateTime,isRead,bodyPreview", url, StringComparison.Ordinal);
            Assert.DoesNotContain("$filter=", url, StringComparison.Ordinal);
            Assert.DoesNotContain("$orderby=", url, StringComparison.Ordinal);
            Assert.Equal(2, url.Count(character => character == '"'));
            Assert.Contains(arguments.RootElement.TryGetProperty("receivedAfter", out _)
                ? "$search=\"invoice AND received>=2026-10-01T00:00:00Z\""
                : "$search=\"invoice\"", url, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("$orderby=receivedDateTime desc", url, StringComparison.Ordinal);
            Assert.Equal(arguments.RootElement.TryGetProperty("receivedAfter", out _), url.Contains("$filter=receivedDateTime ge", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task DraftAndSendReturnTheToolTextWithoutAView()
    {
        var handler = new ScriptedHandler
        {
            Respond = static request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/sendMail", StringComparison.Ordinal) || path.EndsWith("/send", StringComparison.Ordinal))
                {
                    return JsonResponse("{}");
                }

                return JsonResponse("""{"id":"draft-1","subject":"Hello","from":{"emailAddress":{"address":"me@example.com"}}}""");
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"to":"owner@example.com","subject":"Hello","body":"See you there."}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult draft = await MailToolExecutor.CallAsync(
            "mail_create_draft",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);
        Assert.False(draft.IsError, draft.Text);
        Assert.Null(draft.StructuredJson);
        Assert.Contains("draft-1", draft.Text, StringComparison.Ordinal);

        MailToolResult sent = await MailToolExecutor.CallAsync(
            "mail_send",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);
        Assert.False(sent.IsError, sent.Text);
        Assert.Null(sent.StructuredJson);
        Assert.Contains("\"sent\":true", sent.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadListsAttachmentNamesWithoutFileBytes()
    {
        var calls = new List<string>();
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                calls.Add(path);
                if (path.EndsWith("/$value", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent("hello"u8.ToArray()),
                    };
                }

                if (path.Contains("/attachments/", StringComparison.Ordinal))
                {
                    return JsonResponse(
                        """
                        {"id":"att-1","name":"notes.txt","contentType":"text/plain","size":5,"@odata.type":"#microsoft.graph.fileAttachment"}
                        """);
                }

                if (path.Contains("/attachments", StringComparison.Ordinal))
                {
                    return JsonResponse(
                        """
                        {"value":[{"id":"att-1","name":"notes.txt","contentType":"text/plain","size":5,"isInline":false}]}
                        """);
                }

                return JsonResponse(
                    """
                    {"id":"msg-1","subject":"Invoice","from":{"emailAddress":{"address":"owner@example.com"}},"toRecipients":[],"ccRecipients":[],"isDraft":false,"body":{"contentType":"Text","content":"See attached"}}
                    """);
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-1"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_read",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.NotNull(result.StructuredJson);
        Assert.Contains("notes.txt", result.StructuredJson, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"att-1\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"notes.txt\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"contentType\":\"text/plain\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"sizeBytes\":5", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("aGVsbG8=", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", result.Text, StringComparison.Ordinal);
        Assert.Contains(calls, call => call.EndsWith("/messages/msg-1/attachments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadSavesOriginalHtmlAndEmbeddedImage()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-mail-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var queries = new List<string>();
            var handler = new ScriptedHandler
            {
                Respond = request =>
                {
                    queries.Add(request.RequestUri?.Query ?? string.Empty);
                    string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                    if (path.EndsWith("/$value", StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent([1, 2, 3]),
                        };
                    }

                    if (path.Contains("/attachments/", StringComparison.Ordinal))
                    {
                        return JsonResponse(
                            """
                            {"id":"att-1","name":"logo.png","contentType":"image/png","size":3,"@odata.type":"#microsoft.graph.fileAttachment"}
                            """);
                    }

                    if (path.Contains("/attachments", StringComparison.Ordinal))
                    {
                        return JsonResponse(
                            """
                            {"value":[{"id":"att-1","name":"logo.png","contentType":"image/png","size":3,"isInline":true,"contentId":"logo"}]}
                            """);
                    }

                    return JsonResponse(
                        """
                        {"id":"msg-1","subject":"Hello","receivedDateTime":"2026-10-06T00:00:00Z","from":{"emailAddress":{"address":"owner@example.com"}},"toRecipients":[],"ccRecipients":[],"isDraft":false,"body":{"contentType":"html","content":"<p>Hi</p><img src='cid:logo'>"}}
                        """);
                },
            };
            using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-1"}""");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
            };

            MailToolResult result = await MailToolExecutor.CallAsync(
                "mail_read",
                arguments.RootElement,
                environment,
                handler,
                CancellationToken.None).ConfigureAwait(true);

            Assert.False(result.IsError, result.Text);
            Assert.Contains(queries, query => query.Contains("microsoft.graph.fileAttachment/contentId", StringComparison.Ordinal));
            Assert.DoesNotContain(queries, query => query.Contains(",contentId", StringComparison.Ordinal) || query.Contains("=contentId", StringComparison.Ordinal));
            Assert.DoesNotContain("cid:logo", result.Text, StringComparison.Ordinal);
            Assert.Contains("Hi", result.Text, StringComparison.Ordinal);
            Assert.NotNull(result.StructuredJson);
            using JsonDocument structured = JsonDocument.Parse(result.StructuredJson);
            string htmlBase64 = structured.RootElement.GetProperty("files").EnumerateArray()
                .Single(file => file.GetProperty("name").GetString() == "message.html")
                .GetProperty("base64").GetString()!;
            string html = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(htmlBase64));
            Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
            Assert.Contains("Hi", html, StringComparison.Ordinal);
            Assert.DoesNotContain("cid:logo", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<p>Hi</p>", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadLetterOmitsStylesheetAndKeepsTheHtmlFile()
    {
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/attachments", StringComparison.Ordinal))
                {
                    return JsonResponse("""{"value":[]}""");
                }

                return JsonResponse(
                    """
                    {"id":"msg-1","subject":"Weekly Product Update","receivedDateTime":"2026-10-06T11:18:00Z","from":{"emailAddress":{"address":"developer@example.com"}},"toRecipients":[{"emailAddress":{"address":"am.shukla@example.com"}}],"ccRecipients":[],"isDraft":false,"bodyPreview":"Read the latest example product update.","body":{"contentType":"html","content":"<html><head><style>.m1{display:inline-block!important;width:100%!important}</style></head><body><p>Read the latest example product update.</p></body></html>"}}
                    """);
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-1"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_read",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("inline-block", result.Text, StringComparison.Ordinal);
        Assert.Contains("Read the latest example product update.", result.Text, StringComparison.Ordinal);
        using JsonDocument structured = JsonDocument.Parse(result.StructuredJson!);
        string letter = structured.RootElement.GetProperty("view").GetProperty("blocks")[0].GetProperty("body").GetString()!;
        Assert.DoesNotContain("inline-block", letter, StringComparison.Ordinal);
        Assert.Contains("Read the latest example product update.", letter, StringComparison.Ordinal);
        string htmlBase64 = structured.RootElement.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("name").GetString() == "message.html")
            .GetProperty("base64").GetString()!;
        string html = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(htmlBase64));
        Assert.Contains("inline-block", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAttachmentReturnsDownloadableFile()
    {
        var calls = new List<string>();
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                calls.Add(path);
                if (path.EndsWith("/$value", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent("hello"u8.ToArray()),
                    };
                }

                return JsonResponse(
                    """
                    {"id":"att-1","name":"notes.txt","contentType":"text/plain","size":5,"@odata.type":"#microsoft.graph.fileAttachment"}
                    """);
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-1","attachmentId":"att-1"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_read_attachment",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("notes.txt", result.Text, StringComparison.Ordinal);
        Assert.NotNull(result.StructuredJson);
        Assert.Contains("aGVsbG8=", result.StructuredJson, StringComparison.Ordinal);
        Assert.DoesNotContain("aGVsbG8=", result.Text, StringComparison.Ordinal);
        Assert.Contains(calls, call => call.EndsWith("/attachments/att-1/$value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAttachmentSavesFileWithoutReturningBytes()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-mail-").FullName;
        try
        {
            var handler = new ScriptedHandler
            {
                Respond = request =>
                {
                    string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                    if (path.EndsWith("/$value", StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent("hello"u8.ToArray()),
                        };
                    }

                    return JsonResponse(
                        """
                        {"id":"att-1","name":"notes.txt","contentType":"text/plain","size":5,"@odata.type":"#microsoft.graph.fileAttachment"}
                        """);
                },
            };
            using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-1","attachmentId":"att-1"}""");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
                ["KAUTUKA_WORKSPACE"] = root,
            };

            MailToolResult result = await MailToolExecutor.CallAsync(
                "mail_read_attachment",
                arguments.RootElement,
                environment,
                handler,
                CancellationToken.None).ConfigureAwait(true);

            Assert.False(result.IsError, result.Text);
            Assert.NotNull(result.StructuredJson);
            Assert.Contains("aGVsbG8=", result.StructuredJson, StringComparison.Ordinal);
            Assert.DoesNotContain("aGVsbG8=", result.Text, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(root, "notes.txt", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StdioDoesNotGiveTheSandboxAWorkspace()
    {
        Environment.SetEnvironmentVariable("KAUTUKA_WORKSPACE", "/kautuka/mail");
        Environment.SetEnvironmentVariable("KAUTUKA_M365_ACCESS_TOKEN", "access");
        try
        {
            Dictionary<string, string> environment = MailMcpStdioServer.ReadToolEnvironment();
            Assert.False(environment.ContainsKey("KAUTUKA_WORKSPACE"));
            Assert.Equal("access", environment["KAUTUKA_M365_ACCESS_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KAUTUKA_WORKSPACE", null);
            Environment.SetEnvironmentVariable("KAUTUKA_M365_ACCESS_TOKEN", null);
        }
    }

    [Fact]
    public async Task ReadAttachmentRejectsFilesOverEightMegabytes()
    {
        var handler = new ScriptedHandler
        {
            Respond = request =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/$value", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("File bytes were requested.");
                }

                return JsonResponse(
                    """
                    {"id":"att-1","name":"large.bin","contentType":"application/octet-stream","size":40000000,"@odata.type":"#microsoft.graph.fileAttachment"}
                    """);
            },
        };
        using JsonDocument arguments = JsonDocument.Parse("""{"messageId":"msg-1","attachmentId":"att-1"}""");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KAUTUKA_M365_ACCESS_TOKEN"] = "access",
        };

        MailToolResult result = await MailToolExecutor.CallAsync(
            "mail_read_attachment",
            arguments.RootElement,
            environment,
            handler,
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.IsError);
        Assert.Contains("32 MB", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdioListsToolsAndRejectsUnknownMethods()
    {
        static Task<MailToolResult> Unused(string name, JsonElement arguments, CancellationToken cancellationToken)
            => Task.FromResult(new MailToolResult(false, "unused"));

        string? initialize = await MailMcpStdioServer.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            Unused,
            CancellationToken.None).ConfigureAwait(true);
        using JsonDocument initialized = JsonDocument.Parse(initialize!);
        Assert.Equal("2025-06-18", initialized.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());

        string? notification = await MailMcpStdioServer.HandleLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Unused,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Null(notification);

        string? unknown = await MailMcpStdioServer.HandleLineAsync(
            """{"jsonrpc":"2.0","id":2,"method":"resources/list"}""",
            Unused,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Contains("-32601", unknown, StringComparison.Ordinal);

        string? tools = await MailMcpStdioServer.HandleLineAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""",
            Unused,
            CancellationToken.None).ConfigureAwait(true);
        Assert.Contains("mail_send", tools, StringComparison.Ordinal);
        Assert.Contains("mail_read_attachment", tools, StringComparison.Ordinal);
        Assert.Contains("destructiveHint", tools, StringComparison.Ordinal);

        string? download = await MailMcpStdioServer.HandleLineAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"mail_read_attachment","arguments":{}}}""",
            static (_, _, _) => Task.FromResult(new MailToolResult(
                false,
                "Downloaded notes.txt (5 bytes). The owner can download it from this chat.",
                """{"fileName":"notes.txt","contentType":"text/plain","sizeBytes":5,"base64":"aGVsbG8="}""")),
            CancellationToken.None).ConfigureAwait(true);
        Assert.NotNull(download);
        Assert.Contains("structuredContent", download, StringComparison.Ordinal);
        Assert.Contains("notes.txt", download, StringComparison.Ordinal);
        int textAt = download.IndexOf("\"text\"", StringComparison.Ordinal);
        int structuredAt = download.IndexOf("structuredContent", StringComparison.Ordinal);
        Assert.True(textAt >= 0 && structuredAt > textAt);
        Assert.DoesNotContain("aGVsbG8=", download[..structuredAt], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("not-a-guid", null)]
    [InlineData("11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111")]
    public void ReadClientIdRejectsPlaceholder(string? configured, string? expected)
    {
        Assert.Equal(expected, ConnectorAuthProfile.ReadClientId(configured));
    }

    [Fact]
    public void PublicHttpReturnOriginIsRejected()
    {
        Assert.False(ConnectorAuthProfile.TryNormalizeReturnOrigin("http://mail.example.com", out _));
        Assert.True(ConnectorAuthProfile.TryNormalizeReturnOrigin("http://127.0.0.1:20229", out string loopback));
        Assert.Equal("http://127.0.0.1:20229", loopback);
        Assert.True(ConnectorAuthProfile.TryNormalizeReturnOrigin("http://kautuka.local:5000", out string mdns));
        Assert.Equal("http://kautuka.local:5000", mdns);
        Assert.False(ConnectorAuthProfile.TryNormalizeReturnOrigin("http://gateway.office.local", out _));
        Assert.False(ConnectorAuthProfile.TryNormalizeReturnOrigin("http://-invalid.local", out _));
        Assert.False(ConnectorAuthProfile.TryNormalizeReturnOrigin("http://invalid-.local", out _));
        Assert.True(ConnectorAuthProfile.TryNormalizeReturnOrigin("https://kautuka.example.com", out _));
    }

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

        throw new InvalidOperationException("The start URL has no state.");
    }

    private static ConnectorAuthProfile Profile(string? clientId, bool hostedKey = false) => new()
    {
        Id = "kautuka-m365-mail",
        Name = "Microsoft 365 mail",
        Description = "Read, draft, and send mail.",
        Grant = "authorization_code",
        AuthorizeUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        Scopes = "offline_access Mail.ReadWrite Mail.Send User.Read",
        RedirectUri = "https://auth.kautuka.io/callback",
        StartUrl = "https://auth.kautuka.io/start",
        RefreshCredentialKey = "KAUTUKA_M365_REFRESH_TOKEN",
        AccessTokenEnvironmentKey = "KAUTUKA_M365_ACCESS_TOKEN",
        RevokeUrl = "https://myaccount.microsoft.com/permissions",
        SignInLabel = "Sign in with Microsoft",
        EntryPoint = "kautuka-m365-mail",
        ToolNames = ["mail_list", "mail_read", "mail_create_draft", "mail_update_draft", "mail_send"],
        AskFirst = ["mail_send"],
        ClientId = clientId,
        UseDpop = hostedKey,
        DpopKeyCredentialKey = hostedKey ? "KAUTUKA_M365_DPOP_KEY" : null,
    };

    private static string DecodeJwtPayload(string jwt)
    {
        string part = jwt.Split('.')[1];
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

        public string? SavedDpopKey { get; private set; }

        public Task<string?> GetAsync(string userId, string key, CancellationToken cancellationToken)
            => Task.FromResult(SavedRefreshToken);

        public Task SetAsync(string userId, string key, string refreshToken, CancellationToken cancellationToken)
        {
            if (key == "KAUTUKA_M365_DPOP_KEY")
            {
                SavedDpopKey = refreshToken;
            }
            else
            {
                SavedRefreshToken = refreshToken;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string userId, string key, CancellationToken cancellationToken)
        {
            SavedRefreshToken = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public required Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Respond(request));
        }
    }
}
