import { HardDrive } from 'lucide-react';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import type { ApplianceHealth } from '../schemas/applianceHealthSchema';

interface ApplianceHealthCardProps {
  health: ApplianceHealth | undefined;
}

function fmtTimestamp(iso: string | null | undefined): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  } catch {
    return iso;
  }
}

export function ApplianceHealthCard({ health }: Readonly<ApplianceHealthCardProps>) {
  return (
    <Card className="card-interactive dashboard-health-card min-w-0 overflow-hidden border-border/80 bg-card/95">
      <CardHeader className="p-5 pb-3">
        <div className="mb-2 inline-flex items-center gap-2 text-muted-foreground">
          <HardDrive className="h-4 w-4 text-primary" />
          <CardDescription>Appliance</CardDescription>
        </div>
        <CardTitle
          className="break-all text-lg leading-snug"
          title={health?.installedVersion ?? undefined}
        >
          {health?.installedVersion ?? '—'}
        </CardTitle>
      </CardHeader>
      <CardContent className="dashboard-health-card-content space-y-2 p-5 pt-0">
        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">Last update</span>
          <span className="font-medium">{fmtTimestamp(health?.lastUpdateAppliedAtUtc)}</span>
        </div>
        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">Sidecars staged</span>
          <span className="font-medium">
            {health?.sidecarCount != null ? `${health.sidecarCount} (${fmtTimestamp(health.sidecarsStagedAtUtc)})` : '—'}
          </span>
        </div>
      </CardContent>
    </Card>
  );
}
