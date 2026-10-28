import { AlertCircle, CheckCircle2, Circle, Loader2, Pencil, Plug, Trash2 } from 'lucide-react';
import React from 'react';
import { HitlFunctionStat, useHitlStats } from '@/features/approvals';
import { CatalogCard, CatalogIcon } from '@/components/layout/CatalogCard';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { StatePill } from '@/components/ui/state-pill';
import { useConnectorCredentialStatus, useConnectorOAuth } from '../api/queries';
import type { Connector } from '../schemas/connectorSchema';
import { ConnectorSignInPanel, matchesConnectorOAuth } from './MailSignInPanel';

interface ConnectorCardProps {
  connector: Connector;
  canManage: boolean;
  onToggle: (id: string, enabled: boolean) => void;
  onEdit: (connector: Connector) => void;
  onDelete: (connector: Connector) => void;
  toggling: boolean;
}

export const ConnectorCard: React.FC<ConnectorCardProps> = ({
  connector,
  canManage,
  onToggle,
  onEdit,
  onDelete,
  toggling,
}) => {
  const oauth = useConnectorOAuth();
  const managed = (oauth.data ?? []).some((status) => matchesConnectorOAuth(connector.id, status));
  const isHttp = connector.transport === 'http';
  const badge = isHttp ? 'HTTP' : 'Container';
  const scopeBadge = connector.scope === 'system' ? 'System' : 'User';
  const commandSummary = managed
    ? ''
    : isHttp
      ? (connector.httpUrl ?? '')
      : [connector.containerCommand, ...connector.containerArgs].filter(Boolean).join(' ');

  const hasEnvKeys = !managed && (connector.envKeys?.length ?? 0) > 0;
  const { data: credStatus, isLoading: credLoading } = useConnectorCredentialStatus(
    hasEnvKeys ? connector.id : null
  );
  const hitlStats = useHitlStats({ mcpServerId: connector.id });
  const hitlTools = (connector.toolNames ?? []).filter((toolName) =>
    (hitlStats.data?.items ?? []).some((item) => item.toolName === toolName)
  );

  const credBadge = (() => {
    if (!hasEnvKeys) return null;
    if (credLoading)
      return (
        <span className="flex items-center gap-1 text-[10px] text-muted-foreground">
          <Loader2 className="h-3 w-3 animate-spin" />
          creds
        </span>
      );
    if (!credStatus) return null;
    const total = credStatus.configuredKeys.length + credStatus.missingKeys.length;
    const allSet = credStatus.missingKeys.length === 0;
    return allSet ? (
      <Badge variant="success" className="gap-1">
        <CheckCircle2 className="h-3 w-3" />
        {total}/{total} creds
      </Badge>
    ) : (
      <Badge variant="attention" className="gap-1">
        <AlertCircle className="h-3 w-3" />
        {credStatus.configuredKeys.length}/{total} creds
      </Badge>
    );
  })();

  return (
    <CatalogCard
      className="catalog-card-uniform"
      icon={
        connector.iconUrl ? (
          <img
            src={connector.iconUrl}
            alt={connector.name}
            className="h-10 w-10 shrink-0 rounded-lg object-contain"
          />
        ) : (
          <CatalogIcon>
            <Plug className="h-5 w-5 text-muted-foreground" />
          </CatalogIcon>
        )
      }
      title={connector.name}
      badges={
        <>
          <Badge variant="muted">{badge}</Badge>
          <Badge variant="muted">{scopeBadge}</Badge>
          {connector.toolCount != null ? (
            <Badge variant="info">{connector.toolCount} tools</Badge>
          ) : null}
          {credBadge}
        </>
      }
      description={connector.description}
      meta={
        <>
          {commandSummary ? (
            <p className="mt-1 truncate font-mono text-[10px] text-muted-foreground/70" title={commandSummary}>
              {commandSummary}
            </p>
          ) : null}
          <div className="mt-1">
            <HitlFunctionStat mcpServerId={connector.id} label={connector.name} />
          </div>
        </>
      }
      actions={
        managed ? null : (
          <>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="text-muted-foreground"
              onClick={() => onEdit(connector)}
              title="Edit"
            >
              <Pencil className="h-3.5 w-3.5" />
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="text-muted-foreground hover:text-destructive"
              onClick={() => onDelete(connector)}
              disabled={!canManage}
              title={canManage ? 'Delete' : 'Only admins can delete system connectors'}
            >
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
          </>
        )
      }
      footer={
        <>
          <span className="max-w-[60%] truncate font-mono text-xs text-muted-foreground">{connector.id}</span>
          <StatePill
            active={connector.enabled}
            disabled={toggling || !canManage}
            onClick={() => onToggle(connector.id, !connector.enabled)}
            title={canManage ? undefined : 'Only admins can change system connector settings'}
          >
            {connector.enabled ? (
              <>
                <CheckCircle2 className="h-3.5 w-3.5" /> Enabled
              </>
            ) : (
              <>
                <Circle className="h-3.5 w-3.5" /> Disabled
              </>
            )}
          </StatePill>
        </>
      }
    >
      <ConnectorSignInPanel connectorId={connector.id} />
      {hitlTools.length > 0 ? (
        <div
          role="region"
          tabIndex={0}
          className="workspace-scroll-bounded space-y-1 border-t border-border pt-2 pr-1"
          aria-label={`${connector.name} function activity`}
        >
          {hitlTools.map((toolName) => (
            <div key={toolName} className="flex items-center justify-between gap-2">
              <span className="truncate font-mono text-[11px] text-foreground/75">{toolName}</span>
              <HitlFunctionStat
                mcpServerId={connector.id}
                toolName={toolName}
                label={toolName}
              />
            </div>
          ))}
        </div>
      ) : null}
    </CatalogCard>
  );
};
