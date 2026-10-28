import React from 'react';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { useCurrentUser } from '@/features/auth';
import { useUsageRuns, useUsageSummary, type UsageSummaryGroupBy } from '@/features/usage';
import { ROLE_ADMIN, useAgents, useModels } from '@/features/settings';
import { BarChart3, Bot, ChevronDown, ChevronUp, Clock3, DatabaseZap, MessageSquareText } from 'lucide-react';

type UsageRangePreset =
  | 'last15m'
  | 'last1h'
  | 'today'
  | 'yesterday'
  | 'last7d'
  | 'last30d'
  | 'custom';

function formatNumber(value: number | null | undefined): string {
  return new Intl.NumberFormat().format(value ?? 0);
}

function formatDuration(durationMs: number | null | undefined): string {
  const value = durationMs ?? 0;
  if (value < 1000) {
    return `${value} ms`;
  }

  const seconds = value / 1000;
  if (seconds < 60) {
    return `${seconds.toFixed(1)} s`;
  }

  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = Math.round(seconds % 60);
  return `${minutes}m ${remainingSeconds}s`;
}

function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString();
}

function getGroupLabel(groupBy: UsageSummaryGroupBy): string {
  switch (groupBy) {
    case 'user':
      return 'Top users';
    case 'model':
      return 'Top models';
    case 'source':
      return 'Top sources';
    case 'provider':
      return 'Top providers';
    case 'agent':
      return 'Top agents';
    case 'session':
      return 'Top sessions';
    case 'date':
    default:
      return 'Daily usage';
  }
}

function toLocalDateTimeInputValue(value: Date): string {
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, '0');
  const day = String(value.getDate()).padStart(2, '0');
  const hours = String(value.getHours()).padStart(2, '0');
  const minutes = String(value.getMinutes()).padStart(2, '0');
  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

function startOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate(), 0, 0, 0, 0);
}

function endOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate(), 23, 59, 59, 999);
}

function resolvePresetRange(preset: UsageRangePreset): { from: Date | null; to: Date | null } {
  const now = new Date();

  switch (preset) {
    case 'last15m':
      return { from: new Date(now.getTime() - 15 * 60 * 1000), to: now };
    case 'last1h':
      return { from: new Date(now.getTime() - 60 * 60 * 1000), to: now };
    case 'today':
      return { from: startOfDay(now), to: now };
    case 'yesterday': {
      const yesterday = new Date(now);
      yesterday.setDate(now.getDate() - 1);
      return { from: startOfDay(yesterday), to: endOfDay(yesterday) };
    }
    case 'last7d':
      return { from: new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000), to: now };
    case 'last30d':
      return { from: new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000), to: now };
    case 'custom':
    default:
      return { from: null, to: null };
  }
}

function toIsoOrUndefined(value: string): string | undefined {
  if (!value) {
    return undefined;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}

function getPresetLabel(preset: UsageRangePreset): string {
  switch (preset) {
    case 'last15m':
      return 'Last 15 mins';
    case 'last1h':
      return 'Last hour';
    case 'today':
      return 'Today';
    case 'yesterday':
      return 'Yesterday';
    case 'last7d':
      return 'Last 7 days';
    case 'last30d':
      return 'Last 30 days';
    case 'custom':
    default:
      return 'Custom';
  }
}

function buildSourceOptions(sources: Array<string | null | undefined>): Array<{ value: string; label: string }> {
  const knownSources = ['chat', 'telegram', 'autopilot'];
  const uniqueValues = new Set<string>();

  for (const source of knownSources) {
    uniqueValues.add(source);
  }

  for (const source of sources) {
    if (source) {
      uniqueValues.add(source);
    }
  }

  return [
    { value: '', label: 'All sources' },
    ...Array.from(uniqueValues)
      .sort((left, right) => left.localeCompare(right))
      .map((value) => ({ value, label: value })),
  ];
}

function divideOrZero(total: number | null | undefined, count: number | null | undefined): number {
  if (!total || !count) {
    return 0;
  }

  return total / count;
}

function formatFilterSummary(
  scope: 'self' | 'all',
  preset: UsageRangePreset,
  agentLabel: string,
  modelLabel: string,
  sourceLabel: string
): string {
  return [scope === 'all' ? 'All users' : 'My usage', getPresetLabel(preset), agentLabel, modelLabel, sourceLabel].join(' • ');
}

export const UsagePage: React.FC = () => {
  const runsPageSize = 10;
  const { data: currentUser } = useCurrentUser();
  const { data: agents = [] } = useAgents();
  const { data: models = [] } = useModels();
  const isAdmin = currentUser?.role === ROLE_ADMIN;
  const [scope, setScope] = React.useState<'self' | 'all'>('self');
  const [groupBy, setGroupBy] = React.useState<UsageSummaryGroupBy>('date');
  const [rangePreset, setRangePreset] = React.useState<UsageRangePreset>('last7d');
  const initialPresetRange = React.useRef(resolvePresetRange('last7d'));
  const [presetRange, setPresetRange] = React.useState(initialPresetRange.current);
  const [customFrom, setCustomFrom] = React.useState(
    toLocalDateTimeInputValue(initialPresetRange.current.from ?? new Date())
  );
  const [customTo, setCustomTo] = React.useState(
    toLocalDateTimeInputValue(initialPresetRange.current.to ?? new Date())
  );
  const [agentIdFilter, setAgentIdFilter] = React.useState('');
  const [modelIdFilter, setModelIdFilter] = React.useState('');
  const [sourceFilter, setSourceFilter] = React.useState('');
  const [isFilterOpen, setIsFilterOpen] = React.useState(false);
  const [runsPage, setRunsPage] = React.useState(1);
  const effectiveScope = isAdmin ? scope : 'self';
  const availableGroups: UsageSummaryGroupBy[] =
    isAdmin && effectiveScope === 'all' ? ['user', 'date', 'model', 'source'] : ['date', 'model', 'source'];
  const activeFrom = rangePreset === 'custom' ? toIsoOrUndefined(customFrom) : presetRange.from?.toISOString();
  const activeTo = rangePreset === 'custom' ? toIsoOrUndefined(customTo) : presetRange.to?.toISOString();

  React.useEffect(() => {
    if (!availableGroups.includes(groupBy)) {
      setGroupBy(availableGroups[0]);
    }
  }, [availableGroups, groupBy]);

  React.useEffect(() => {
    if (rangePreset !== 'custom') {
      setPresetRange(resolvePresetRange(rangePreset));
    }
  }, [rangePreset]);

  React.useEffect(() => {
    setRunsPage(1);
  }, [effectiveScope, activeFrom, activeTo, agentIdFilter, modelIdFilter, sourceFilter]);

  const summary = useUsageSummary({
    scope: effectiveScope,
    groupBy,
    bucketLimit: 6,
    agentId: agentIdFilter,
    modelId: modelIdFilter,
    source: sourceFilter,
    fromTime: activeFrom,
    toTime: activeTo,
  });
  const runs = useUsageRuns({
    scope: effectiveScope,
    limit: runsPageSize,
    offset: (runsPage - 1) * runsPageSize,
    agentId: agentIdFilter,
    modelId: modelIdFilter,
    source: sourceFilter,
    fromTime: activeFrom,
    toTime: activeTo,
  });
  const agentOptions = [
    { value: '', label: 'All agents' },
    ...agents
      .slice()
      .sort((left, right) => left.name.localeCompare(right.name) || left.id.localeCompare(right.id))
      .map((agent) => ({ value: agent.id, label: `${agent.name} (${agent.id})` })),
  ];
  const modelOptions = [
    { value: '', label: 'All models' },
    ...models
      .slice()
      .sort((left, right) => left.name.localeCompare(right.name) || left.id.localeCompare(right.id))
      .map((model) => ({ value: model.id, label: `${model.name} (${model.id})` })),
  ];
  const sourceOptions = buildSourceOptions(runs.data?.items.map((item) => item.sourceChannel) ?? []);
  const selectedAgentLabel = agentOptions.find((option) => option.value === agentIdFilter)?.label ?? 'All agents';
  const selectedModelLabel = modelOptions.find((option) => option.value === modelIdFilter)?.label ?? 'All models';
  const selectedSourceLabel = sourceOptions.find((option) => option.value === sourceFilter)?.label ?? 'All sources';
  const avgTokensPerRun = divideOrZero(summary.data?.totals.totalTokens, summary.data?.totals.runCount);
  const avgProviderRequestsPerRun = divideOrZero(summary.data?.totals.providerRequestCount, summary.data?.totals.runCount);
  const avgRunDurationMs = divideOrZero(summary.data?.totals.totalDurationMs, summary.data?.totals.runCount);
  const filterSummary = formatFilterSummary(
    effectiveScope,
    rangePreset,
    selectedAgentLabel,
    selectedModelLabel,
    selectedSourceLabel
  );
  const totalRunPages = Math.max(1, Math.ceil((runs.data?.totalCount ?? 0) / runsPageSize));
  const currentRunStart = runs.data?.totalCount ? (runsPage - 1) * runsPageSize + 1 : 0;
  const currentRunEnd = runs.data?.totalCount
    ? Math.min(runsPage * runsPageSize, runs.data?.totalCount ?? 0)
    : 0;

  const errorMessage = summary.error instanceof Error
    ? summary.error.message
    : runs.error instanceof Error
      ? runs.error.message
      : null;

  if ((summary.isPending || runs.isPending) && !summary.data && !runs.data) {
    return (
      <div className="page-shell">
        <PageState message="Loading usage..." />
      </div>
    );
  }

  return (
    <div className="page-shell">
      <PageIntro
        kicker="Observability"
        title="Usage"
        description="Track run volume, token usage, response timing, and model activity."
      />

      <div className="space-y-6">
        <section className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-xl font-bold text-foreground">Usage overview</h2>
            <p className="text-sm text-muted-foreground">
              Focus on run volume, token consumption, provider activity, and response timing for the current slice of usage.
            </p>
          </div>

          {isAdmin ? (
            <div className="inline-flex rounded-full border border-border bg-card/80 p-1">
              <Button
                type="button"
                size="sm"
                variant={effectiveScope === 'self' ? 'default' : 'ghost'}
                onClick={() => setScope('self')}
              >
                My usage
              </Button>
              <Button
                type="button"
                size="sm"
                variant={effectiveScope === 'all' ? 'default' : 'ghost'}
                onClick={() => setScope('all')}
              >
                All users
              </Button>
            </div>
          ) : null}
        </section>

        <section className="rounded-3xl border border-border/80 bg-card/95 p-4 sm:p-5">
          <div className="flex flex-col gap-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 className="text-base font-semibold text-foreground">Filter</h3>
                <p className="text-sm text-muted-foreground">{filterSummary}</p>
              </div>
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => setIsFilterOpen((value) => !value)}
              >
                {isFilterOpen ? 'Hide filters' : 'Show filters'}
                {isFilterOpen ? <ChevronUp className="ml-2 h-4 w-4" /> : <ChevronDown className="ml-2 h-4 w-4" />}
              </Button>
            </div>

            {isFilterOpen ? (
              <>
                <div className="flex flex-wrap gap-2">
                  {(['last15m', 'last1h', 'today', 'yesterday', 'last7d', 'last30d', 'custom'] as UsageRangePreset[]).map((preset) => (
                    <Button
                      key={preset}
                      type="button"
                      size="sm"
                      variant={rangePreset === preset ? 'default' : 'outline'}
                      onClick={() => {
                        if (preset === 'custom') {
                          setCustomFrom(toLocalDateTimeInputValue(presetRange.from ?? new Date()));
                          setCustomTo(toLocalDateTimeInputValue(presetRange.to ?? new Date()));
                        }

                        setRangePreset(preset);
                      }}
                    >
                      {getPresetLabel(preset)}
                    </Button>
                  ))}
                </div>

                {rangePreset === 'custom' ? (
                  <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
                    <label className="space-y-1 text-sm text-muted-foreground">
                      <span>From</span>
                      <Input
                        type="datetime-local"
                        value={customFrom}
                        onChange={(event) => setCustomFrom(event.target.value)}
                      />
                    </label>
                    <label className="space-y-1 text-sm text-muted-foreground">
                      <span>To</span>
                      <Input
                        type="datetime-local"
                        value={customTo}
                        onChange={(event) => setCustomTo(event.target.value)}
                      />
                    </label>
                  </div>
                ) : (
                  <div className="text-sm text-muted-foreground">
                    Active range: {presetRange.from ? formatTimestamp(presetRange.from.toISOString()) : 'Any time'} to {presetRange.to ? formatTimestamp(presetRange.to.toISOString()) : 'Now'}
                  </div>
                )}

                <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
                  <label className="space-y-1 text-sm text-muted-foreground">
                    <span>Agent</span>
                    <Select
                      value={agentIdFilter}
                      onChange={(event) => setAgentIdFilter(event.target.value)}
                      options={agentOptions}
                    />
                  </label>
                  <label className="space-y-1 text-sm text-muted-foreground">
                    <span>Model</span>
                    <Select
                      value={modelIdFilter}
                      onChange={(event) => setModelIdFilter(event.target.value)}
                      options={modelOptions}
                    />
                  </label>
                  <label className="space-y-1 text-sm text-muted-foreground">
                    <span>Source</span>
                    <Select
                      value={sourceFilter}
                      onChange={(event) => setSourceFilter(event.target.value)}
                      options={sourceOptions}
                    />
                  </label>
                </div>

                <p className="text-xs text-muted-foreground">
                  Agent, model, and source filters can be used independently or together. Matching is exact against the recorded usage facts.
                </p>
              </>
            ) : null}
          </div>
        </section>

        {errorMessage ? (
          <Alert variant="destructive" className="p-3">
            {errorMessage}
          </Alert>
        ) : null}

        <section className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-4">
          <Card className="card-interactive border-border/80 bg-card/95">
            <CardHeader className="p-5 pb-3">
              <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
                <BarChart3 className="h-4 w-4 text-primary" />
                <CardDescription>Runs</CardDescription>
              </div>
              <CardTitle className="text-2xl">{formatNumber(summary.data?.totals.runCount)}</CardTitle>
            </CardHeader>
            <CardContent className="p-5 pt-0 text-sm text-muted-foreground">
              Runs matched by the current scope and filters.
            </CardContent>
          </Card>

          <Card className="card-interactive border-border/80 bg-card/95">
            <CardHeader className="p-5 pb-3">
              <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
                <MessageSquareText className="h-4 w-4 text-primary" />
                <CardDescription>Total tokens</CardDescription>
              </div>
              <CardTitle className="text-2xl">{formatNumber(summary.data?.totals.totalTokens)}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-1 p-5 pt-0 text-sm text-muted-foreground">
              <div>Input: {formatNumber(summary.data?.totals.tokenIn)}</div>
              <div>Output: {formatNumber(summary.data?.totals.tokenOut)}</div>
              <div>Average per run: {formatNumber(avgTokensPerRun)}</div>
            </CardContent>
          </Card>

          <Card className="card-interactive border-border/80 bg-card/95">
            <CardHeader className="p-5 pb-3">
              <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
                <DatabaseZap className="h-4 w-4 text-primary" />
                <CardDescription>Provider requests</CardDescription>
              </div>
              <CardTitle className="text-2xl">{formatNumber(summary.data?.totals.providerRequestCount)}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-1 p-5 pt-0 text-sm text-muted-foreground">
              <div>Model turns: {formatNumber(summary.data?.totals.modelTurnCount)}</div>
              <div>Average requests per run: {avgProviderRequestsPerRun.toFixed(1)}</div>
              <div>Tool calls: {formatNumber(summary.data?.totals.toolCallCount)}</div>
            </CardContent>
          </Card>

          <Card className="card-interactive border-border/80 bg-card/95">
            <CardHeader className="p-5 pb-3">
              <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
                <Clock3 className="h-4 w-4 text-primary" />
                <CardDescription>Response timing</CardDescription>
              </div>
              <CardTitle className="text-2xl">
                {formatDuration(summary.data?.totals.avgFirstTokenLatencyMs)}
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-1 p-5 pt-0 text-sm text-muted-foreground">
              <div>Average first response latency</div>
              <div>Average run time: {formatDuration(avgRunDurationMs)}</div>
            </CardContent>
          </Card>
        </section>

        <section className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(20rem,1fr)]">
          <Card className="min-w-0 overflow-hidden border-border/80 bg-card/95">
            <CardHeader className="flex flex-row items-start justify-between gap-3 p-5">
              <div>
                <CardTitle>Recent runs</CardTitle>
                <CardDescription>Latest executions that match the current scope and filters.</CardDescription>
              </div>
              <div className="text-right text-sm text-muted-foreground">
                <div>{formatNumber(runs.data?.totalCount)} total</div>
                <div>
                  Showing {formatNumber(currentRunStart)}-{formatNumber(currentRunEnd)}
                </div>
              </div>
            </CardHeader>
            <CardContent className="min-w-0 p-0">
              {runs.data && runs.data.items.length > 0 ? (
                <div className="max-h-96 w-full overflow-auto">
                  <table className="min-w-full text-sm">
                    <thead className="sticky top-0 bg-muted/95 text-left text-muted-foreground backdrop-blur">
                      <tr>
                        <th className="px-4 py-3 font-medium">Started</th>
                        <th className="px-4 py-3 font-medium">Agent / model</th>
                        <th className="px-4 py-3 font-medium">Source</th>
                        {effectiveScope === 'all' ? <th className="px-4 py-3 font-medium">User</th> : null}
                        <th className="px-4 py-3 font-medium">Input</th>
                        <th className="px-4 py-3 font-medium">Output</th>
                        <th className="px-4 py-3 font-medium">Total</th>
                        <th className="px-4 py-3 font-medium">Requests</th>
                        <th className="px-4 py-3 font-medium">Tool calls</th>
                        <th className="px-4 py-3 font-medium">Duration</th>
                      </tr>
                    </thead>
                    <tbody>
                      {runs.data.items.map((item) => (
                        <tr key={item.runId} className="border-t border-border/60 align-top">
                          <td className="px-4 py-3 text-muted-foreground">{formatTimestamp(item.runStartedAt)}</td>
                          <td className="px-4 py-3">
                            <div className="font-medium text-foreground">{item.agentNameSnapshot ?? item.agentId ?? 'Unknown agent'}</div>
                            <div className="text-muted-foreground">{item.modelNameSnapshot ?? item.modelId ?? 'Unknown model'}</div>
                          </td>
                          <td className="px-4 py-3 text-muted-foreground">{item.sourceChannel ?? 'unknown'}</td>
                          {effectiveScope === 'all' ? (
                            <td className="px-4 py-3 text-muted-foreground">
                              {item.userDisplayNameSnapshot ?? item.usernameSnapshot ?? item.userId ?? 'Unknown user'}
                            </td>
                          ) : null}
                          <td className="px-4 py-3 text-muted-foreground">{formatNumber(item.tokenIn)}</td>
                          <td className="px-4 py-3 text-muted-foreground">{formatNumber(item.tokenOut)}</td>
                          <td className="px-4 py-3 text-muted-foreground">{formatNumber(item.totalTokens)}</td>
                          <td className="px-4 py-3 text-muted-foreground">
                            <div>Provider: {formatNumber(item.providerRequestCount)}</div>
                            <div>Model: {formatNumber(item.modelTurnCount)}</div>
                          </td>
                          <td className="px-4 py-3 text-muted-foreground">{formatNumber(item.toolCallCount)}</td>
                          <td className="px-4 py-3 text-muted-foreground">{formatDuration(item.totalDurationMs)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <div className="p-6 text-sm text-muted-foreground">No usage has been recorded yet.</div>
              )}

              {runs.data && runs.data.totalCount > 0 ? (
                <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border/60 px-5 py-4">
                  <div className="text-sm text-muted-foreground">
                    Page {formatNumber(runsPage)} of {formatNumber(totalRunPages)}
                  </div>
                  <div className="flex items-center gap-2">
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={runsPage <= 1}
                      onClick={() => setRunsPage((page) => Math.max(1, page - 1))}
                    >
                      Previous
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={runsPage >= totalRunPages}
                      onClick={() => setRunsPage((page) => Math.min(totalRunPages, page + 1))}
                    >
                      Next
                    </Button>
                  </div>
                </div>
              ) : null}
            </CardContent>
          </Card>

          <Card className="border-border/80 bg-card/95">
            <CardHeader className="space-y-3 p-5">
              <div>
                <CardTitle>{getGroupLabel(groupBy)}</CardTitle>
                <CardDescription>Break down usage to see where volume and spend drivers are concentrated.</CardDescription>
              </div>
              <div className="flex flex-wrap gap-2">
                {availableGroups.map((option) => (
                  <Button
                    key={option}
                    type="button"
                    size="sm"
                    variant={groupBy === option ? 'default' : 'outline'}
                    onClick={() => setGroupBy(option)}
                  >
                    {option}
                  </Button>
                ))}
              </div>
            </CardHeader>
            <CardContent className="space-y-3 p-5 pt-0">
              {summary.data && summary.data.buckets.length > 0 ? (
                summary.data.buckets.map((bucket) => (
                  <div key={bucket.key} className="rounded-2xl border border-border/70 bg-muted/20 p-3">
                    <div className="flex items-start justify-between gap-3">
                      <div>
                        <div className="font-medium text-foreground">{bucket.label}</div>
                        <div className="text-xs text-muted-foreground">{formatNumber(bucket.totals.runCount)} runs</div>
                      </div>
                      <Bot className="h-4 w-4 text-primary" />
                    </div>
                    <div className="mt-3 grid grid-cols-2 gap-2 text-xs text-muted-foreground">
                      <div>Tokens: {formatNumber(bucket.totals.totalTokens)}</div>
                      <div>Provider req: {formatNumber(bucket.totals.providerRequestCount)}</div>
                      <div>Model rounds: {formatNumber(bucket.totals.modelTurnCount)}</div>
                      <div>Avg latency: {formatDuration(bucket.totals.avgFirstTokenLatencyMs)}</div>
                    </div>
                  </div>
                ))
              ) : (
                <div className="rounded-xl border border-dashed border-border bg-muted/20 p-4 text-sm text-muted-foreground">
                  No grouped usage data available yet.
                </div>
              )}
            </CardContent>
          </Card>
        </section>
      </div>
    </div>
  );
};

export default UsagePage;