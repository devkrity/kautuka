import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { SegmentedControl } from '@/components/ui/segmented-control';
import { HitlFunctionStat } from '@/features/approvals';
import { matchesConnectorOAuth, useConnectorCredentialStatus, useConnectorOAuth, type Connector } from '@/features/connectors';
import { AlertCircle, CheckCircle2, ChevronRight, Plug } from 'lucide-react';
import type { ReactNode } from 'react';

export type ConnectorMode = 'off' | 'on' | 'advanced';
export type ToolMode = 'off' | 'on' | 'ask';

interface McpPanelProps {
  ownerId: string;
  connector: Connector;
  mode: ConnectorMode;
  selectionEnabled?: boolean;
  isPanelOpen: boolean;
  onModeChange: (mode: ConnectorMode) => void;
  onTogglePanel: () => void;
  getToolMode: (toolName: string) => ToolMode;
  onToolModeChange: (toolName: string, mode: ToolMode) => void;
  footer?: ReactNode;
}

export function McpPanel({
  ownerId,
  connector,
  mode,
  selectionEnabled = true,
  isPanelOpen,
  onModeChange,
  onTogglePanel,
  getToolMode,
  onToolModeChange,
  footer,
}: Readonly<McpPanelProps>) {
  const knownTools = connector.toolNames ?? [];
  const oauth = useConnectorOAuth();
  const managed = (oauth.data ?? []).some((status) => matchesConnectorOAuth(connector.id, status));
  const hasEnvKeys = !managed && (connector.envKeys?.length ?? 0) > 0;
  const { data: credStatus } = useConnectorCredentialStatus(
    hasEnvKeys && mode !== 'off' ? connector.id : null
  );

  return (
    <div
      className={[
        'rounded-xl border border-border/50 bg-card overflow-hidden shadow-sm',
        selectionEnabled ? '' : 'opacity-65',
      ].join(' ')}
    >
      <div className="px-4 py-3">
        <div className="flex items-center gap-3">
          {connector.iconUrl ? (
            <img
              src={connector.iconUrl}
              alt={connector.name}
              className="h-7 w-7 rounded-lg object-contain shrink-0"
            />
          ) : (
            <div className="h-7 w-7 rounded-lg bg-muted flex items-center justify-center shrink-0">
              <Plug className="h-3.5 w-3.5 text-muted-foreground" />
            </div>
          )}

          <div className="text-sm font-medium leading-snug break-words">{connector.name}</div>
        </div>

        <div className="mt-2 flex justify-end">
          <SegmentedControl
            value={mode}
            disabled={!selectionEnabled}
            onChange={onModeChange}
            options={[
              { value: 'off', label: 'Disabled', tone: 'neutral' },
              { value: 'on', label: 'Trusted', tone: 'primary' },
              { value: 'advanced', label: 'Per tool', tone: 'attention' },
            ]}
          />
        </div>
      </div>

      {mode === 'advanced' && (
        <div className="border-t border-border/50">
          {knownTools.length === 0 ? (
            <p className="px-4 py-3 text-xs text-muted-foreground">
              We don&apos;t know what tools this connector has yet. Go to{' '}
              <strong>Connectors</strong>, open it and click <strong>Test</strong>.
            </p>
          ) : (
            <>
              <Button
                type="button"
                variant="ghost"
                className="flex w-full justify-start rounded-none px-4 py-2 h-auto text-xs text-muted-foreground hover:bg-muted/40"
                onClick={onTogglePanel}
                disabled={!selectionEnabled}
              >
                <ChevronRight
                  className={[
                    'h-3.5 w-3.5 transition-transform duration-200',
                    isPanelOpen ? 'rotate-90' : '',
                  ].join(' ')}
                />
                <span>{knownTools.length} tools</span>
              </Button>
              {isPanelOpen && (
                <div className="pb-2">
                  {knownTools.map((toolName, i) => {
                    const toolMode = getToolMode(toolName);
                    return (
                      <div
                        key={toolName}
                        className={[
                          'px-4 py-2',
                          i > 0 ? 'border-t border-border/30' : '',
                        ].join(' ')}
                      >
                        <div className="text-xs font-mono text-foreground/75 leading-snug break-words">
                          {toolName}
                        </div>
                        <div className="mt-1">
                          <HitlFunctionStat
                            agentId={ownerId}
                            mcpServerId={connector.id}
                            toolName={toolName}
                            label={toolName}
                          />
                        </div>
                        <div className="mt-1.5 flex justify-end">
                          <div className="flex items-center gap-0.5 shrink-0">
                            <SegmentedControl
                              shape="pill"
                              size="xs"
                              value={toolMode}
                              disabled={!selectionEnabled}
                              onChange={(next) => onToolModeChange(toolName, next)}
                              options={[
                                { value: 'on', label: 'Allowed', tone: 'info' },
                                { value: 'ask', label: 'Ask me first', tone: 'attention-soft' },
                                { value: 'off', label: 'Blocked', tone: 'muted' },
                              ]}
                            />
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </>
          )}
        </div>
      )}

      {mode !== 'off' && hasEnvKeys && (
        <div className="border-t border-border/50 px-4 py-3">
          <div className="mb-2 text-xs font-medium text-muted-foreground">Required Credentials</div>
          <div className="flex flex-wrap gap-2">
            {(connector.envKeys ?? []).map((key) => {
              const isConfigured = credStatus?.configuredKeys.includes(key);
              const isMissing = credStatus?.missingKeys.includes(key);
              return (
                <Badge
                  key={`${ownerId}-${connector.id}-${key}`}
                  variant={isConfigured ? 'success' : isMissing ? 'attention' : 'muted'}
                  className="gap-1 font-mono"
                  title={
                    isConfigured
                      ? 'Credential configured'
                      : 'Credential not set — configure in Connectors'
                  }
                >
                  {isConfigured ? (
                    <CheckCircle2 className="h-3 w-3 shrink-0" />
                  ) : (
                    <AlertCircle className="h-3 w-3 shrink-0" />
                  )}
                  {key}
                </Badge>
              );
            })}
          </div>
          {credStatus && credStatus.missingKeys.length > 0 && (
            <p className="mt-2 text-[11px] text-attention">
              Configure missing credentials in <strong>Connectors</strong> before using this
              connector.
            </p>
          )}
        </div>
      )}

      {footer ? <div className="border-t border-border/50 px-4 py-3">{footer}</div> : null}
    </div>
  );
}
