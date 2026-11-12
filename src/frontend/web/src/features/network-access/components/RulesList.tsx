import React from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { formatDestinationDisplay } from '../destinationScope';
import type { NetworkAccessRuleDto } from '../networkAccess';

export function getRuleScopeSentence(rule: NetworkAccessRuleDto): string {
  const user = rule.userDisplayName || rule.userId || 'User';
  const agent = rule.agentDisplayName || rule.agentId || 'Agent';
  const connector = rule.connectorDisplayName || rule.connectorId || 'Connector';

  if (rule.destKind === 'any-public') {
    return `${user}'s ${connector} can reach any public website`;
  }
  if (rule.paused) {
    return `Paused: ${user} can no longer allow for all agents`;
  }
  if (rule.scopeKind === 'agent') {
    return `${user}'s ${agent}`;
  }
  if (rule.scopeKind === 'user') {
    if (rule.connectorDisplayName || rule.connectorId) {
      return `Only ${user}, using ${connector}`;
    }
    return `All of ${user}'s agents, including new ones`;
  }
  if (rule.scopeKind === 'connector-system') {
    return `Everyone using ${connector}`;
  }
  return `${user}'s ${agent}`;
}

interface RulesListProps {
  rules: NetworkAccessRuleDto[];
  expectedRevision: number;
  currentUser?: { id?: string; displayName?: string | null; role?: string };
  onDeleteRule: (ruleId: string, expectedRevision: number) => Promise<unknown>;
  onBlockSeedRule?: (rule: NetworkAccessRuleDto) => Promise<unknown>;
  disabled?: boolean;
}

export function RulesList({
  rules,
  expectedRevision,
  currentUser,
  onDeleteRule,
  onBlockSeedRule,
  disabled = false,
}: RulesListProps) {
  const isAdmin = currentUser?.role?.toLowerCase() === 'admin';
  const [activeTab, setActiveTab] = React.useState<'allowed' | 'blocked' | 'seed'>('allowed');
  const [submittingRuleId, setSubmittingRuleId] = React.useState<string | null>(null);

  // Group rules:
  // - seed: source === 'seed'
  // - allowed: source !== 'seed' && allowed === true
  // - blocked: source !== 'seed' && allowed === false
  // Paused rules are typically in allowed or blocked, shown with paused message
  const seedRules = rules.filter((r) => r.source === 'seed');
  const allowedRules = rules.filter((r) => r.source !== 'seed' && r.allowed);
  const blockedRules = rules.filter((r) => r.source !== 'seed' && !r.allowed);

  const handleDelete = async (ruleId: string) => {
    setSubmittingRuleId(ruleId);
    try {
      await onDeleteRule(ruleId, expectedRevision);
    } finally {
      setSubmittingRuleId(null);
    }
  };

  const handleBlockSeed = async (rule: NetworkAccessRuleDto) => {
    if (!onBlockSeedRule) return;
    setSubmittingRuleId(rule.id);
    try {
      await onBlockSeedRule(rule);
    } finally {
      setSubmittingRuleId(null);
    }
  };

  const renderRuleItem = (rule: NetworkAccessRuleDto, isSeed: boolean = false) => {
    const scopeSentence = getRuleScopeSentence(rule);
    const destDisplay =
      rule.destKind === 'any-public'
        ? 'Any public website'
        : formatDestinationDisplay(rule.destination, rule.port);

    return (
      <article
        key={rule.id}
        data-testid={`rule-row-${rule.id}`}
        className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 rounded-xl border border-border bg-card p-3.5 shadow-sm"
      >
        <div className="space-y-1 min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium text-sm text-foreground break-all">
              {destDisplay}
            </span>

            {rule.paused && (
              <Badge variant="outline" className="text-xs">
                Paused
              </Badge>
            )}

            {rule.openInternetBadge && (
              <Badge variant="attention" className="text-xs">
                Open internet
              </Badge>
            )}

            {rule.allowLan && (
              <Badge variant="outline" className="text-xs">
                Local network included
              </Badge>
            )}

            {isSeed && (
              <Badge variant="secondary" className="text-xs">
                Built in
              </Badge>
            )}
          </div>

          <p className="text-sm text-muted-foreground">{scopeSentence}</p>
        </div>

        <div className="flex items-center gap-2 shrink-0">
          {rule.paused ? (
            // Paused rule: Remove button only (no Keep/Enable control)
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={disabled || submittingRuleId === rule.id}
              onClick={() => void handleDelete(rule.id)}
            >
              Remove
            </Button>
          ) : isSeed ? (
            // Built in (seed) rules cannot be deleted; can only be blocked by admin
            isAdmin && rule.allowed && onBlockSeedRule && (
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={disabled || submittingRuleId === rule.id}
                onClick={() => void handleBlockSeed(rule)}
              >
                Block
              </Button>
            )
          ) : (
            // Normal rule: Remove button
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={disabled || submittingRuleId === rule.id}
              onClick={() => void handleDelete(rule.id)}
            >
              Remove
            </Button>
          )}
        </div>
      </article>
    );
  };

  return (
    <div className="space-y-4">
      <Tabs value={activeTab} onValueChange={(v) => setActiveTab(v as typeof activeTab)}>
        <TabsList className="grid grid-cols-3 w-full max-w-md">
          <TabsTrigger value="allowed">Allowed ({allowedRules.length})</TabsTrigger>
          <TabsTrigger value="blocked">Blocked ({blockedRules.length})</TabsTrigger>
          <TabsTrigger value="seed">Built in ({seedRules.length})</TabsTrigger>
        </TabsList>

        <TabsContent value="allowed" className="space-y-2.5 pt-2">
          {allowedRules.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4">No allowed rules yet.</p>
          ) : (
            allowedRules.map((rule) => renderRuleItem(rule, false))
          )}
        </TabsContent>

        <TabsContent value="blocked" className="space-y-2.5 pt-2">
          {blockedRules.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4">No blocked rules.</p>
          ) : (
            blockedRules.map((rule) => renderRuleItem(rule, false))
          )}
        </TabsContent>

        <TabsContent value="seed" className="space-y-2.5 pt-2">
          <p className="text-xs text-muted-foreground mb-2">
            Built-in permissions keep Kautuka services working. Built-in rules cannot be deleted,
            but administrators can block them.
          </p>
          {seedRules.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4">No built-in rules found.</p>
          ) : (
            seedRules.map((rule) => renderRuleItem(rule, true))
          )}
        </TabsContent>
      </Tabs>
    </div>
  );
}
