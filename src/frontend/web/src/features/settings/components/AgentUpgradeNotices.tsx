import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import type { AgentSettingsEntry } from '../types/settingsTypes';

export function AgentUpgradeNotices({ agent, onAcknowledge, className = '' }: Readonly<{
  agent: Pick<AgentSettingsEntry, 'notices' | 'canAcknowledgeNotices' | 'acknowledgedNotices'>;
  onAcknowledge?: () => void;
  className?: string;
}>) {
  if (!agent.notices?.length) return null;
  return (
    <Alert variant="info" className={`space-y-1 px-3 py-2 text-xs ${className}`}>
      {agent.notices.map((notice) => <p key={notice}>{notice}</p>)}
      {!agent.canAcknowledgeNotices ? (
        <p>The agent's owner will see this notice and can dismiss it.</p>
      ) : (agent.acknowledgedNotices?.length ?? 0) > 0 ? (
        <p>Save your changes to dismiss this notice.</p>
      ) : onAcknowledge ? (
        <Button type="button" variant="outline" size="sm" onClick={onAcknowledge}>
          Dismiss after saving
        </Button>
      ) : <p>To dismiss this notice, open the agent's settings and select Connectors.</p>}
    </Alert>
  );
}
