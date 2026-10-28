import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ConversationTargetCard } from '@/components/conversation/ConversationTargetCard';
import { createSession, useRecentSessions } from '@/features/chat';
import { useSystemHealth, useApplianceHealth, ApplianceHealthCard } from '@/features/dashboard';
import { useAgents } from '@/features/settings';
import { useAutopilotSchedules, useAutopilotRuns } from '@/features/autopilot';
import { PageIntro } from '@/components/layout/PageIntro';
import { useProgressiveLimit } from '@/components/layout/useProgressiveLimit';
import { ShowMoreButton } from '@/components/layout/ShowMoreButton';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Activity, Loader2, Radio, Timer, Workflow } from 'lucide-react';
import { toast } from 'sonner';

function healthVariant(status: string): 'success' | 'muted' | 'danger' {
  if (status === 'healthy') {
    return 'success';
  }

  if (status === 'not_configured') {
    return 'muted';
  }

  return 'danger';
}

function getTelegramStatusLabel(status: string) {
  if (status === 'healthy') {
    return 'Linked';
  }

  if (status === 'not_configured') {
    return 'Not linked';
  }

  return 'Unavailable';
}

function getWorkspaceStatusLabel(status: string) {
  if (status === 'healthy') {
    return 'Reachable';
  }

  if (status === 'unknown') {
    return 'Unknown';
  }

  return 'Down';
}

function getLifecycleLabel(status: string) {
  if (status === 'agents_ready') {
    return 'Agents Ready';
  }

  if (status === 'agents_pending') {
    return 'Agents Pending';
  }

  return 'Core Not Ready';
}

function renderActiveRunCount(activeRuns: number) {
  if (activeRuns === 0) {
    return 'None';
  }

  return (
    <span className="inline-flex items-center gap-1 text-primary">
      <Loader2 className="h-3 w-3 animate-spin" />
      {activeRuns}
    </span>
  );
}

export const Dashboard = () => {
  const navigate = useNavigate();
  const [launchingTarget, setLaunchingTarget] = useState<string | null>(null);
  const { data: agents = [] } = useAgents();
  const { data: recentSessions = [] } = useRecentSessions(100);
  const { data: systemHealth } = useSystemHealth();
  const { data: applianceHealth } = useApplianceHealth();
  const { data: schedules = [] } = useAutopilotSchedules();
  const { data: runs = [] } = useAutopilotRuns();
  const {
    visibleItems: visibleAgents,
    hiddenCount: hiddenAgentCount,
    showMore: showMoreAgents,
  } = useProgressiveLimit(agents);

  const activeRuns = runs.filter((r) => r.stateCategory !== 'terminal');
  const recentFailedRuns = runs
    .filter((r) => ['failed', 'rejected', 'timed_out'].includes(r.status))
    .slice(0, 3);
  const enabledSchedules = schedules.filter((s: { mode?: string }) => s.mode !== 'off');
  const runtimeStatus = (systemHealth?.runtime.status ?? 'unknown').toLowerCase();
  const runtimeHealthy = runtimeStatus === 'healthy';
  const telegramStatus = (systemHealth?.channels.status ?? 'unknown').toLowerCase();
  const workspaceStatus = (systemHealth?.workspace.status ?? 'unknown').toLowerCase();
  const lifecycleStatus = (systemHealth?.lifecycle.status ?? 'core_not_ready').toLowerCase();
  const actionHints = systemHealth?.actionHints ?? [];
  const driftActions = systemHealth?.driftActions ?? [];
  let lifecycleStatusClass = 'unhealthy';
  if (lifecycleStatus === 'agents_ready') {
    lifecycleStatusClass = 'healthy';
  } else if (lifecycleStatus === 'agents_pending') {
    lifecycleStatusClass = 'not_configured';
  }

  const runtimeTitle = runtimeHealthy
    ? 'Gateway reachable'
    : lifecycleStatus === 'agents_pending' || lifecycleStatus === 'core_not_ready'
      ? 'Appliance starting'
      : 'Gateway unavailable';
  const runtimeDescription = runtimeHealthy
    ? 'Core web surface is live and the gateway is answering requests.'
    : lifecycleStatus === 'agents_pending' || lifecycleStatus === 'core_not_ready'
      ? 'The web UI is up. Core services are still warming in the background.'
      : 'The web UI loaded, but the gateway is not answering yet.';

  let recentIssuesLabel = 'Running clean';
  if (recentFailedRuns.length > 0) {
    const issuePluralSuffix = recentFailedRuns.length == 1 ? '' : 's';
    recentIssuesLabel = `${recentFailedRuns.length} recent issue${issuePluralSuffix}`;
  }
  const sessionsByTarget = new Map<string, (typeof recentSessions)[number]>();

  for (const session of [...recentSessions].sort(
    (left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime()
  )) {
    const key = `${session.targetType}:${session.targetId}`;
    if (!sessionsByTarget.has(key)) {
      sessionsByTarget.set(key, session);
    }
  }

  async function handleStartSession(targetId: string) {
    const targetKey = `agent:${targetId}`;
    setLaunchingTarget(targetKey);

    try {
      const session = await createSession('agent', targetId);
      navigate(
        `/workspace/${encodeURIComponent(targetId)}?sessionId=${encodeURIComponent(session.id)}`
      );
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Could not create the conversation.');
    } finally {
      setLaunchingTarget(null);
    }
  }

  function handleContinueSession(sessionId: string, targetId: string) {
    navigate(
      `/workspace/${encodeURIComponent(targetId)}?sessionId=${encodeURIComponent(sessionId)}`
    );
  }

  return (
    <div className="page-shell">
      <PageIntro
        kicker="Dashboard Overview"
        title="Welcome back"
        description="Check platform status, then jump into the right agent without digging through the agent workspace."
      />

      <div className="space-y-6">
        <section className="space-y-3">
          <div>
            <h2 className="text-xl font-bold text-foreground">System Health</h2>
            <p className="text-sm text-muted-foreground">
              A quick view of gateway reachability, automation activity, and connected services.
            </p>
          </div>

          <div className="grid grid-cols-1 items-stretch gap-3 md:grid-cols-2 xl:grid-cols-4">
            <Card className="card-interactive dashboard-health-card border-border/80 bg-card/95">
              <CardHeader className="p-5 pb-3">
                <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
                  <Activity className="h-4 w-4 text-primary" />
                  <CardDescription>Runtime</CardDescription>
                </div>
                <CardTitle className="text-lg">{runtimeTitle}</CardTitle>
              </CardHeader>
              <CardContent className="dashboard-health-card-content space-y-2 p-5 pt-0">
                <p className="text-sm text-muted-foreground">{runtimeDescription}</p>
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">Service</span>
                  <span className="font-semibold">
                    {systemHealth?.runtime.service ?? 'Gateway'}
                  </span>
                </div>
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">Status</span>
                  <Badge variant={runtimeHealthy ? 'success' : 'danger'}>
                    {systemHealth?.runtime.status ?? 'unknown'}
                  </Badge>
                </div>
              </CardContent>
            </Card>

            <Card className="card-interactive dashboard-health-card border-border/80 bg-card/95">
              <CardHeader className="p-5 pb-3">
                <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
                  <Timer className="h-4 w-4 text-primary" />
                  <CardDescription>Auto-Pilot</CardDescription>
                </div>
                <CardTitle className="text-lg">
                  {enabledSchedules.length > 0 ? 'Automation active' : 'No active schedules'}
                </CardTitle>
              </CardHeader>
              <CardContent className="dashboard-health-card-content space-y-2 p-5 pt-0">
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">Active schedules</span>
                  <span className="font-semibold">
                    {enabledSchedules.length} / {schedules.length}
                  </span>
                </div>
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">In-progress runs</span>
                  <span className="font-semibold">{renderActiveRunCount(activeRuns.length)}</span>
                </div>
                <Badge
                  variant={recentFailedRuns.length > 0 ? 'danger' : 'success'}
                  className="w-fit"
                >
                  {recentIssuesLabel}
                </Badge>
              </CardContent>
            </Card>

            <Card className="card-interactive dashboard-health-card border-border/80 bg-card/95">
              <CardHeader className="p-5 pb-3">
                <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
                  <Workflow className="h-4 w-4 text-primary" />
                  <CardDescription>Operations</CardDescription>
                </div>
                <CardTitle className="text-lg">Connected services</CardTitle>
              </CardHeader>
              <CardContent className="dashboard-health-card-content space-y-2 p-5 pt-0">
                <div className="flex items-center justify-between text-sm">
                  <span className="inline-flex items-center gap-2 text-muted-foreground">
                    <Radio className="h-3.5 w-3.5" />
                    Telegram
                  </span>
                  <Badge variant={healthVariant(telegramStatus)}>
                    {getTelegramStatusLabel(telegramStatus)}
                  </Badge>
                </div>
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">Workspace service</span>
                  <Badge variant={healthVariant(workspaceStatus)}>
                    {getWorkspaceStatusLabel(workspaceStatus)}
                  </Badge>
                </div>
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">Lifecycle</span>
                  <Badge variant={healthVariant(lifecycleStatusClass)}>
                    {getLifecycleLabel(lifecycleStatus)}
                  </Badge>
                </div>
                {driftActions.length > 0 ? (
                  <ul
                    aria-label="Connected service actions"
                    tabIndex={0}
                    className="workspace-scroll-bounded space-y-1 text-xs text-muted-foreground"
                  >
                    {driftActions.map((action) => (
                      <li key={action} className="break-words">
                        {action}
                      </li>
                    ))}
                  </ul>
                ) : null}
                {actionHints.length > 0 ? (
                  <p className="workspace-scroll-bounded-compact break-words text-xs text-muted-foreground">
                    Suggested actions: {actionHints.join(', ')}
                  </p>
                ) : null}
              </CardContent>
            </Card>

            <ApplianceHealthCard health={applianceHealth} />
          </div>
        </section>

        <div className="space-y-4">
          <section className="space-y-3">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
              <div>
                <h2 className="text-xl font-bold text-foreground">Agents</h2>
                <p className="text-sm text-muted-foreground">
                  One-click launch for direct conversations with individual specialists.
                </p>
              </div>
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => navigate('/settings?tab=agents')}
              >
                Manage agents
              </Button>
            </div>

            {agents.length === 0 ? (
              <Card className="workspace-card-subtle">
                <CardContent className="p-4 text-sm text-muted-foreground">
                  No agents configured yet.
                </CardContent>
              </Card>
            ) : (
              <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-[repeat(auto-fit,minmax(15.5rem,18rem))] xl:justify-start">
                {visibleAgents.map((agent) => {
                  const session = sessionsByTarget.get(`agent:${agent.id}`);
                  return (
                    <ConversationTargetCard
                      key={agent.id}
                      targetType="agent"
                      id={agent.id}
                      name={agent.name}
                      subtitle=""
                      badges={[]}
                      lastSession={
                        session
                          ? {
                              id: session.id,
                              title: session.title,
                              updatedAt: session.updatedAt,
                              messagesCount: session.messages.length,
                            }
                          : undefined
                      }
                      launching={launchingTarget === `agent:${agent.id}`}
                      onStartSession={() => {
                        void handleStartSession(agent.id);
                      }}
                      onContinueSession={
                        session ? () => handleContinueSession(session.id, agent.id) : undefined
                      }
                    />
                  );
                })}
              </div>
            )}
            <ShowMoreButton count={hiddenAgentCount} onClick={showMoreAgents} />
          </section>
        </div>
      </div>
    </div>
  );
};
