import React from 'react';
import { ArrowRight, Bot, Boxes, ShieldCheck, Waypoints } from 'lucide-react';
import { Button } from '@/components/ui/button';

type SettingsOverviewProps = {
  onNavigate: (tab: 'providers' | 'models' | 'agents' | 'users') => void;
};

const setupSteps = [
  {
    tab: 'providers' as const,
    icon: Waypoints,
    title: 'Connect a provider',
    body: 'Choose where your models run and add credentials if needed.',
  },
  {
    tab: 'models' as const,
    icon: Boxes,
    title: 'Choose a model',
    body: 'Select a model from a connected provider.',
  },
  {
    tab: 'agents' as const,
    icon: Bot,
    title: 'Configure an agent',
    body: 'Set its behavior, tools, memory, and runtime access.',
  },
  {
    tab: 'users' as const,
    icon: ShieldCheck,
    title: 'Manage access',
    body: 'Invite people and choose who can administer the system.',
  },
];

export const SettingsOverview: React.FC<SettingsOverviewProps> = ({ onNavigate }) => (
  <div className="space-y-5">
    <section className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4" aria-label="Configuration order">
      {setupSteps.map(({ tab, icon: Icon, title, body }, index) => (
        <button
          key={tab}
          type="button"
          onClick={() => onNavigate(tab)}
          className="group rounded-xl border border-border bg-card p-4 text-left transition-colors hover:border-primary/40 hover:bg-accent/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <div className="mb-3 flex items-center justify-between">
            <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-accent text-primary">
              <Icon className="h-4 w-4" />
            </span>
            <span className="text-xs font-medium text-muted-foreground">{index + 1} of 4</span>
          </div>
          <h3 className="font-semibold text-foreground">{title}</h3>
          <p className="mt-1 min-h-10 text-sm text-muted-foreground">{body}</p>
          <span className="mt-3 inline-flex items-center gap-1 text-sm font-medium text-primary">
            Open <ArrowRight className="h-4 w-4 transition-transform group-hover:translate-x-0.5" />
          </span>
        </button>
      ))}
    </section>
    <div className="rounded-xl border border-border/70 bg-muted/30 px-4 py-3 text-sm text-muted-foreground">
      Each agent uses one model, and each model is linked to one provider. Kautuka prevents deleting
      a provider or model while other configuration still depends on it.
    </div>
    <div className="rounded-xl border border-border/70 bg-card px-4 py-3 text-sm text-muted-foreground">
      Administrators manage system configuration; users own their personal agent workspaces. Each
      agent controls its own tools, memory, approvals, and session behavior.
    </div>
    <div className="flex justify-end">
      <Button variant="outline" onClick={() => onNavigate('agents')}>
        Open agent settings <ArrowRight className="ml-2 h-4 w-4" />
      </Button>
    </div>
  </div>
);
