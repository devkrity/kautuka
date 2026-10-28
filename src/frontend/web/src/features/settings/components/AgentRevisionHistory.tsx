import { Button } from '@/components/ui/button';
import { useAgentRevisionDiff, useAgentRevisions } from '../api/queries';
import React from 'react';

type AgentRevisionHistoryProps = {
  agentId: string;
};

function formatRevisionTime(createdAt: string): string {
  const parsed = Date.parse(createdAt);
  if (Number.isNaN(parsed)) {
    return createdAt;
  }

  return new Date(parsed).toLocaleString();
}

function formatSettingValue(value: unknown): string {
  if (value == null) {
    return '—';
  }

  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }

  return JSON.stringify(value, null, 2);
}

function AgentRevisionDiffDetails({
  agentId,
  revision,
}: Readonly<{ agentId: string; revision: number }>) {
  const { data, isLoading } = useAgentRevisionDiff(agentId, revision);

  if (isLoading) {
    return <p className="text-sm text-muted-foreground">Loading changes...</p>;
  }

  if (data == null) {
    return <p className="text-sm text-muted-foreground">Unable to load this revision.</p>;
  }

  if (data.fromRevision == null) {
    return <p className="text-sm text-muted-foreground">Initial configuration.</p>;
  }

  if (data.changes.length === 0) {
    return <p className="text-sm text-muted-foreground">No configuration changes.</p>;
  }

  return (
    <div className="space-y-3">
      {data.changes.map((change) => (
        <div key={change.settingId} className="rounded-md border border-border/60 p-3">
          <p className="mb-2 text-sm font-medium">{change.settingName}</p>
          <div className="grid gap-2 sm:grid-cols-2">
            <div>
              <p className="section-label mb-1">Before</p>
              <pre className="overflow-x-auto whitespace-pre-wrap break-words rounded bg-muted/40 p-2 text-xs">
                {formatSettingValue(change.before)}
              </pre>
            </div>
            <div>
              <p className="section-label mb-1">After</p>
              <pre className="overflow-x-auto whitespace-pre-wrap break-words rounded bg-muted/40 p-2 text-xs">
                {formatSettingValue(change.after)}
              </pre>
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

export function AgentRevisionHistory({ agentId }: Readonly<AgentRevisionHistoryProps>) {
  const { data, isLoading } = useAgentRevisions(agentId);
  const [expandedRevision, setExpandedRevision] = React.useState<number | null>(null);

  if (isLoading) {
    return (
      <section className="mt-8 border-t border-border/50 pt-6">
        <h3 className="text-sm font-semibold">Configuration history</h3>
        <p className="mt-2 text-sm text-muted-foreground">Loading revisions...</p>
      </section>
    );
  }

  const revisions = data?.revisions ?? [];
  if (revisions.length === 0) {
    return null;
  }

  return (
    <section className="mt-8 border-t border-border/50 pt-6">
      <div className="mb-3 flex items-baseline justify-between gap-3">
        <h3 className="text-sm font-semibold">Configuration history</h3>
        <p className="text-xs text-muted-foreground">Revision {data?.currentRevision}</p>
      </div>
      <ol className="space-y-2">
        {revisions.map((revision) => {
          const isExpanded = expandedRevision === revision.revision;
          const summary =
            revision.changedSettingNames.length > 0
              ? revision.changedSettingNames.join(', ')
              : revision.revision === 1
                ? 'Initial configuration'
                : 'No configuration changes';
          return (
            <li key={revision.revision} className="rounded-md border border-border/50">
              <div className="flex items-start justify-between gap-3 px-3 py-2">
                <div>
                  <p className="text-sm font-medium">
                    Revision {revision.revision}
                    <span className="ml-2 font-normal text-muted-foreground">
                      {formatRevisionTime(revision.createdAt)}
                    </span>
                  </p>
                  <p className="text-sm text-muted-foreground">{summary}</p>
                </div>
                <Button
                  type="button"
                  variant="secondary"
                  onClick={() =>
                    setExpandedRevision((current) =>
                      current === revision.revision ? null : revision.revision
                    )
                  }
                >
                  {isExpanded ? 'Hide' : 'Inspect'}
                </Button>
              </div>
              {isExpanded ? (
                <div className="border-t border-border/50 px-3 py-3">
                  <AgentRevisionDiffDetails agentId={agentId} revision={revision.revision} />
                </div>
              ) : null}
            </li>
          );
        })}
      </ol>
    </section>
  );
}
