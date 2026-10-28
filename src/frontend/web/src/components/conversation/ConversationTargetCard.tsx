import React from 'react';
import { History, Loader2, MessageSquarePlus, UserRound } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { cn } from '@/lib/utils';

type BadgeTone = 'muted' | 'info' | 'success';

export interface ConversationTargetCardBadge {
  label: string;
  tone?: BadgeTone;
}

export interface ConversationTargetCardSession {
  id: string;
  title: string;
  updatedAt: string;
  messagesCount: number;
}

interface ConversationTargetCardProps {
  targetType: 'agent';
  id: string;
  name: string;
  subtitle: string;
  badges: ConversationTargetCardBadge[];
  lastSession?: ConversationTargetCardSession;
  launching?: boolean;
  active?: boolean;
  layout?: 'dashboard' | 'sidebar';
  onSelect?: () => void;
  onStartSession: () => void;
  onContinueSession?: () => void;
}

export function ConversationTargetCard({
  targetType,
  id,
  name,
  subtitle,
  badges,
  lastSession,
  launching = false,
  active = false,
  layout = 'dashboard',
  onSelect,
  onStartSession,
  onContinueSession,
}: Readonly<ConversationTargetCardProps>) {
  const Icon = UserRound;
  const isSidebar = layout === 'sidebar';

  return (
    <Card
      className={cn(
        'card-interactive border-border/80 bg-card/95',
        isSidebar ? 'overflow-hidden hover-lift' : 'hover-lift h-full',
        active && 'border-primary/50 bg-primary/[0.04] ring-1 ring-primary/15'
      )}
    >
      <CardHeader className={cn(isSidebar ? 'space-y-2 p-3 pb-2' : 'space-y-3 p-5 pb-3')}>
        <button
          type="button"
          onClick={onSelect}
          disabled={!onSelect}
          className={cn('w-full text-left', onSelect ? 'cursor-pointer' : 'cursor-default')}
        >
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0 flex items-center gap-3">
              <span
                className={cn(
                  'rounded-lg border border-border/70 bg-muted/50 text-primary',
                  isSidebar ? 'p-1.5' : 'p-2'
                )}
              >
                <Icon className="h-4 w-4" />
              </span>
              <div className="min-w-0">
                <CardTitle className={cn('truncate', isSidebar ? 'text-sm' : 'text-lg')}>
                  {name || id}
                </CardTitle>
              </div>
            </div>
            {active ? <Badge variant="info">Current</Badge> : null}
          </div>
        </button>

        {subtitle ? (
          <p
            className={cn(
              'text-muted-foreground',
              isSidebar ? 'line-clamp-2 text-[11px] leading-4' : 'line-clamp-2 text-sm leading-5'
            )}
          >
            {subtitle}
          </p>
        ) : null}

        <div className={cn('gap-1.5', isSidebar ? 'grid grid-cols-1' : 'grid grid-cols-2')}>
          {badges.map((badge) => (
            <Badge
              key={badge.label}
              variant={badge.tone ?? 'muted'}
              className="min-w-0 justify-start overflow-hidden"
            >
              <span className="truncate">{badge.label}</span>
            </Badge>
          ))}
        </div>
      </CardHeader>

      <CardContent className={cn('space-y-3 pt-0', isSidebar ? 'p-3 pt-0' : 'p-5 pt-0')}>
        <div className="flex gap-2">
          <Button
            size="sm"
            onClick={onStartSession}
            disabled={launching}
            title="New chat"
            className={cn(isSidebar ? 'h-8 w-8 px-0' : 'h-8 w-8 px-0')}
          >
            {launching ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <MessageSquarePlus className="h-4 w-4" />
            )}
          </Button>
          <Button
            size="sm"
            variant="outline"
            onClick={onContinueSession}
            disabled={!lastSession || !onContinueSession}
            title={lastSession ? `Resume: ${lastSession.title}` : 'No previous session'}
            className="h-8 w-8 px-0"
          >
            <History className="h-4 w-4" />
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
