import React from 'react';
import type { ModelEntry } from '../types/settingsTypes';
import { CapabilityBadge } from './SettingsShared';

export const CapabilityAdvisory: React.FC<{
  capabilities: ModelEntry['capabilities'];
  emptyText?: string;
}> = ({ capabilities, emptyText = 'No capability data — click Check to run probes.' }) => {
  if (!capabilities) {
    return <p className="mt-2 text-xs text-muted-foreground">{emptyText}</p>;
  }

  return (
    <div className="mt-2 space-y-1">
      {capabilities.probeError && (
        <p className="text-xs text-attention">Capability check failed: {capabilities.probeError}</p>
      )}
      {capabilities.agentConversations === false && (
        <p className="text-xs text-attention">This model may not work for agent conversations</p>
      )}
      <div className="flex flex-wrap gap-1">
        <CapabilityBadge label="Agent Chat" value={capabilities.agentConversations} />
        <CapabilityBadge label="Tools" value={capabilities.toolCalling} />
        <CapabilityBadge label="Streaming" value={capabilities.streaming} />
        <CapabilityBadge label="Structured" value={capabilities.structuredJson} />
        <CapabilityBadge label="Reasoning" value={capabilities.reasoningOnlyOutput} />
      </div>
      <p className="text-xs text-muted-foreground">
        {capabilities.probeError ? 'Last attempted' : 'Checked'}{' '}
        {new Date(capabilities.probedAt).toLocaleString()}
      </p>
    </div>
  );
};
