import React from 'react';
import { Card, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';

export function StatsCardGrid({
  recentSessions,
  agents,
  models,
}: {
  recentSessions: number;
  agents: number;
  models: number;
}) {
  return (
    <div className="mb-6 grid grid-cols-1 gap-4 md:grid-cols-3">
      <Card>
        <CardHeader className="pb-2">
          <CardDescription>Recent sessions</CardDescription>
          <CardTitle className="text-3xl">{recentSessions}</CardTitle>
        </CardHeader>
      </Card>
      <Card>
        <CardHeader className="pb-2">
          <CardDescription>Agents</CardDescription>
          <CardTitle className="text-3xl">{agents}</CardTitle>
        </CardHeader>
      </Card>
      <Card>
        <CardHeader className="pb-2">
          <CardDescription>Models</CardDescription>
          <CardTitle className="text-3xl">{models}</CardTitle>
        </CardHeader>
      </Card>
    </div>
  );
}
