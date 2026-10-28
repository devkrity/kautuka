import React from 'react';
import { Card, CardContent } from '@/components/ui/card';
import { MessageSquare } from 'lucide-react';
import type { DashboardRecentSession } from '../hooks/useDashboardData';
import { fmtRelative } from '../hooks/useDashboardData';

interface RecentSessionsListProps {
  sessions: DashboardRecentSession[];
  onOpenSession: (session: DashboardRecentSession) => void;
}

export function RecentSessionsList({ sessions, onOpenSession }: Readonly<RecentSessionsListProps>) {
  return (
    <>
      <h2 className="mb-3 text-xl font-bold">Continue Working</h2>
      {sessions.length === 0 ? (
        <Card>
          <CardContent className="p-5 text-sm text-muted-foreground">
            No recent sessions yet.
          </CardContent>
        </Card>
      ) : (
        <div className="space-y-3">
          {sessions.map((session) => (
            <Card
              key={session.id}
              className="hover-lift cursor-pointer"
              onClick={() => onOpenSession(session)}
            >
              <CardContent className="flex items-start gap-3 p-4">
                <div className="rounded-lg bg-muted p-2">
                  <MessageSquare className="h-4 w-4 text-muted-foreground" />
                </div>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-lg font-semibold leading-tight text-foreground">
                    {session.title}
                  </p>
                  <p className="truncate text-sm text-muted-foreground">
                    {session.targetName} ({session.targetType})
                  </p>
                </div>
                <div className="text-right">
                  <p className="text-xs font-semibold text-foreground">
                    {fmtRelative(session.updatedAt)}
                  </p>
                  <p className="text-[11px] text-muted-foreground">{session.messages} messages</p>
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </>
  );
}
