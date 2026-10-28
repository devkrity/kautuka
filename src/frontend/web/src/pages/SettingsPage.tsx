import React from 'react';
import { Button } from '@/components/ui/button';
import { PageIntro } from '@/components/layout/PageIntro';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import {
  Bot,
  Boxes,
  Building2,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Waypoints,
} from 'lucide-react';
import { SettingsOverview } from './SettingsOverview';
import { useNavigate, useSearchParams } from 'react-router-dom';
import {
  ProvidersTab,
  ModelsTab,
  AgentsTab,
  UsersTab,
  AdvancedTab,
  OrganizationTab,
  type Tab,
} from '@/features/settings';

export const SettingsPage: React.FC = () => {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const requestedTab = searchParams.get('tab');

  const tabs: Array<{
    id: Tab;
    label: string;
    description: string;
    summary: string;
    impact: string;
    icon: React.ComponentType<{ className?: string }>;
  }> = [
    {
      id: 'overview',
      label: 'Overview',
      description: 'How the system works',
      summary:
        'Learn how providers, models, agents, and users fit together and depend on each other.',
      impact:
        'Understanding these relationships helps you design your AI orchestration system effectively.',
      icon: Sparkles,
    },
    {
      id: 'providers',
      label: 'Providers',
      description: 'Host and key configuration',
      summary:
        'Providers are the AI services Kautuka talks to, such as local Ollama or hosted Gemini.',
      impact:
        'If a provider is missing or wrong, every model and agent connected to it can stop working.',
      icon: Waypoints,
    },
    {
      id: 'models',
      label: 'Models',
      description: 'Provider model mappings',
      summary: 'Models are the named AI options your agents can use when they run.',
      impact:
        'Changing a model changes behavior, quality, latency, and which provider runtime is used.',
      icon: Boxes,
    },
    {
      id: 'agents',
      label: 'Agents',
      description: 'Agent behavior and runtime',
      summary:
        'Agents are the individual AI workers with their own instructions, runtime options, and tool access.',
      impact:
        'These settings directly change how each agent answers, how much session context it keeps, and which tools it can use.',
      icon: Bot,
    },
    {
      id: 'users',
      label: 'Users',
      description: 'Capabilities and object grants',
      summary: 'Users are the people allowed to sign in and work inside the Kautuka system.',
      impact:
        'These settings control who gets access, what they can change, and which agents they can run.',
      icon: ShieldCheck,
    },
    {
      id: 'organization',
      label: 'Organization',
      description: 'Company name and industry',
      summary:
        'Organization settings define your company name and industry, used as defaults across all agent personas.',
      impact:
        'Changing these updates the default context injected into agent personas that have no explicit override.',
      icon: Building2,
    },
    {
      id: 'advanced',
      label: 'Advanced',
      description: 'Runtime and telemetry controls',
      summary:
        'Advanced controls govern lower-level runtime and observability behavior for production operations.',
      impact:
        'Incorrect advanced settings can reduce diagnostics depth, affect diagnostics, or change runtime streaming behavior.',
      icon: SlidersHorizontal,
    },
  ];

  const tab = tabs.some((item) => item.id === requestedTab) ? (requestedTab as Tab) : 'overview';

  const handleTabChange = (nextTab: Tab) => {
    setSearchParams(nextTab === 'overview' ? {} : { tab: nextTab });
  };

  const handleAgentAction = () => {
    setSearchParams({ tab: 'agents' }, { replace: true });
  };

  const activeTab = tabs.find((item) => item.id === tab) ?? tabs[0];
  const ActiveIcon = activeTab.icon;

  return (
    <div className="page-shell">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <PageIntro
          kicker="Configuration Workspace"
          title="Refine Your System"
          description="Explore how everything connects, then manage providers, models, agents, and who can access them."
        />
        <div className="mt-4 shrink-0">
          <Button variant="outline" onClick={() => navigate('/onboarding')}>
            Run Onboarding Wizard
          </Button>
        </div>
      </div>

      <Tabs value={tab} onValueChange={(value) => handleTabChange(value as Tab)}>
        <div className="space-y-4">
          <TabsList
            aria-label="Settings sections"
            className="flex h-auto w-full flex-wrap items-stretch justify-start gap-1 rounded-xl border border-border/70 bg-muted/40 p-1"
          >
            {tabs.map((t) => {
              const Icon = t.icon;
              return (
                <TabsTrigger
                  key={t.id}
                  value={t.id}
                  className="min-h-10 justify-start gap-2 rounded-lg border border-transparent px-3 py-2 text-left text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                >
                  <Icon className="h-4 w-4" />
                  <span className="font-medium">{t.label}</span>
                </TabsTrigger>
              );
            })}
          </TabsList>

          <section className="space-y-4">
            <div className="flex items-start gap-3 border-b border-border/60 pb-4">
              <div className="rounded-lg bg-accent p-2 text-primary">
                <ActiveIcon className="h-5 w-5" />
              </div>
              <div className="min-w-0">
                <h2 className="text-lg font-semibold tracking-tight text-foreground">
                  {activeTab.label}
                </h2>
                <p className="mt-0.5 text-sm text-muted-foreground">{activeTab.summary}</p>
              </div>
            </div>

            <TabsContent value="overview" className="mt-0">
              <SettingsOverview onNavigate={handleTabChange} />
            </TabsContent>
            <TabsContent value="providers" className="mt-0">
              <ProvidersTab />
            </TabsContent>
            <TabsContent value="models" className="mt-0">
              <ModelsTab />
            </TabsContent>
            <TabsContent value="agents" className="mt-0">
              <AgentsTab
                requestedAgentId={searchParams.get('agentId')}
                createRequested={searchParams.get('action') === 'create'}
                onRequestedActionHandled={handleAgentAction}
              />
            </TabsContent>
            <TabsContent value="users" className="mt-0">
              <UsersTab />
            </TabsContent>
            <TabsContent value="organization" className="mt-0">
              <OrganizationTab />
            </TabsContent>
            <TabsContent value="advanced" className="mt-0">
              <AdvancedTab />
            </TabsContent>
          </section>
        </div>
      </Tabs>
    </div>
  );
};
