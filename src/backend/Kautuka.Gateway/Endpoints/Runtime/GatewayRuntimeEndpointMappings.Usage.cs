namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    private static readonly HashSet<string> usageSummaryGroups =
    [
        "none",
        "date",
        "session",
        "model",
        "provider",
        "agent",
        "source",
        "user",
    ];

    internal static void MapGatewayRuntimeUsageEndpoints(WebApplication app)
    {
        app.MapGet("/api/runtime/usage/runs", (
            HttpContext context,
            Kautuka.Gateway.Domain.Auth.IGatewayAuthGuard auth,
            Kautuka.Gateway.Domain.Usage.IGatewayUsageQueryStore usageStore) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out Kautuka.Gateway.Domain.Models.GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            if (!TryBuildUsageQuery(context, user, out Kautuka.Gateway.Domain.Usage.GatewayUsageQueryFilter filter, out string scope, out IResult? errorResult))
            {
                return errorResult!;
            }

            GatewayUsageRunListItem[] items = [.. usageStore.ListRuns(filter)];
            long totalCount = usageStore.CountRuns(filter);
            return Results.Json(
                new GatewayUsageRunsResponse(items, filter.Limit, filter.Offset, totalCount, scope),
                KautukaJsonContext.Default.GatewayUsageRunsResponse);
        });

        app.MapGet("/api/runtime/usage/summary", (
            HttpContext context,
            Kautuka.Gateway.Domain.Auth.IGatewayAuthGuard auth,
            Kautuka.Gateway.Domain.Usage.IGatewayUsageQueryStore usageStore) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out Kautuka.Gateway.Domain.Models.GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            if (!TryBuildUsageQuery(context, user, out Kautuka.Gateway.Domain.Usage.GatewayUsageQueryFilter filter, out string scope, out IResult? errorResult))
            {
                return errorResult!;
            }

            string groupBy = Normalize(context.Request.Query["groupBy"].ToString()) ?? "none";
            if (!usageSummaryGroups.Contains(groupBy))
            {
                return Results.Json(
                    new GatewayErrorResponse("Invalid groupBy. Supported values: none, date, session, model, provider, agent, source, user."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            int bucketLimit = ParseInt(context.Request.Query["bucketLimit"].ToString(), 50, 1, 200);
            GatewayUsageTotalsPayload totals = usageStore.GetTotals(filter);
            GatewayUsageSummaryBucket[] buckets = groupBy == "none"
                ? []
                : [.. usageStore.GetBuckets(filter, groupBy, bucketLimit)];

            return Results.Json(
                new GatewayUsageSummaryResponse(scope, groupBy, totals, buckets),
                KautukaJsonContext.Default.GatewayUsageSummaryResponse);
        });
    }

    private static bool TryBuildUsageQuery(
        HttpContext context,
        Kautuka.Gateway.Domain.Models.GatewayUser user,
        out Kautuka.Gateway.Domain.Usage.GatewayUsageQueryFilter filter,
        out string scope,
        out IResult? errorResult)
    {
        errorResult = null;

        if (!TryResolveUsageRange(
                context,
                out string? fromTimestamp,
                out string? toTimestamp,
                out errorResult))
        {
            filter = DefaultFilter(user.Id);
            scope = Kautuka.Gateway.Domain.Auth.GatewayOwnershipAccess.IsAdmin(user) ? "all" : "self";
            return false;
        }

        bool adminAll = ResolveAdminAllScope(user, context.Request.Query["scope"].ToString());
        scope = adminAll ? "all" : "self";
        string effectiveUserId = adminAll
            ? Normalize(context.Request.Query["userId"].ToString()) ?? string.Empty
            : user.Id;

        filter = new Kautuka.Gateway.Domain.Usage.GatewayUsageQueryFilter(
            string.IsNullOrWhiteSpace(effectiveUserId) ? null : effectiveUserId,
            Normalize(context.Request.Query["sessionId"].ToString()),
            Normalize(context.Request.Query["agentId"].ToString()),
            Normalize(context.Request.Query["modelId"].ToString()),
            Normalize(context.Request.Query["providerId"].ToString()),
            Normalize(context.Request.Query["source"].ToString()),
            fromTimestamp,
            toTimestamp,
            ParseInt(context.Request.Query["limit"].ToString(), 50, 1, 200),
            ParseInt(context.Request.Query["offset"].ToString(), 0, 0, int.MaxValue));

        return true;
    }

    private static bool ResolveAdminAllScope(Kautuka.Gateway.Domain.Models.GatewayUser user, string? requestedScope)
        => Kautuka.Gateway.Domain.Auth.GatewayOwnershipAccess.IsAdmin(user)
            && !string.Equals(Normalize(requestedScope), "self", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveUsageRange(
        HttpContext context,
        out string? fromTimestamp,
        out string? toTimestamp,
        out IResult? errorResult)
    {
        fromTimestamp = null;
        toTimestamp = null;
        errorResult = null;

        if (!TryParseUsageTimestamp(context.Request.Query["fromTime"].ToString(), "fromTime", out DateTimeOffset? fromTime, out errorResult)
            || !TryParseUsageTimestamp(context.Request.Query["toTime"].ToString(), "toTime", out DateTimeOffset? toTime, out errorResult)
            || !TryParseUsageDate(context.Request.Query["fromDate"].ToString(), "fromDate", out DateOnly? fromDate, out errorResult)
            || !TryParseUsageDate(context.Request.Query["toDate"].ToString(), "toDate", out DateOnly? toDate, out errorResult))
        {
            return false;
        }

        DateTimeOffset? effectiveFrom = fromTime;
        DateTimeOffset? effectiveTo = toTime;

        if (!effectiveFrom.HasValue && fromDate.HasValue)
        {
            effectiveFrom = new DateTimeOffset(fromDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        if (!effectiveTo.HasValue && toDate.HasValue)
        {
            effectiveTo = new DateTimeOffset(toDate.Value.ToDateTime(new TimeOnly(23, 59, 59, 999)), TimeSpan.Zero);
        }

        if (effectiveFrom.HasValue && effectiveTo.HasValue && effectiveFrom.Value > effectiveTo.Value)
        {
            errorResult = Results.Json(
                new GatewayErrorResponse("Invalid range. fromTime/fromDate must be earlier than or equal to toTime/toDate."),
                KautukaJsonContext.Default.GatewayErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        fromTimestamp = effectiveFrom?.ToUniversalTime().ToString("O");
        toTimestamp = effectiveTo?.ToUniversalTime().ToString("O");
        return true;
    }

    private static bool TryParseUsageDate(string? input, string fieldName, out DateOnly? normalized, out IResult? errorResult)
    {
        string? value = Normalize(input);
        errorResult = null;
        normalized = null;
        if (value is null)
        {
            return true;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateOnly parsed))
        {
            normalized = parsed;
            return true;
        }

        errorResult = Results.Json(
            new GatewayErrorResponse($"Invalid {fieldName}. Expected yyyy-MM-dd."),
            KautukaJsonContext.Default.GatewayErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    private static bool TryParseUsageTimestamp(string? input, string fieldName, out DateTimeOffset? normalized, out IResult? errorResult)
    {
        string? value = Normalize(input);
        errorResult = null;
        normalized = null;
        if (value is null)
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            normalized = parsed;
            return true;
        }

        errorResult = Results.Json(
            new GatewayErrorResponse($"Invalid {fieldName}. Expected ISO 8601 date-time."),
            KautukaJsonContext.Default.GatewayErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    private static int ParseInt(string? input, int defaultValue, int min, int max)
        => int.TryParse(input, out int parsed)
            ? Math.Clamp(parsed, min, max)
            : defaultValue;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Kautuka.Gateway.Domain.Usage.GatewayUsageQueryFilter DefaultFilter(string userId)
        => new(userId, null, null, null, null, null, null, null, 50, 0);
}
