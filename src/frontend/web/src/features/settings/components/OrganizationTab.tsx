import React from 'react';
import { Building2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { PageState } from '@/components/layout/PageState';
import { useOrganizationTab } from '../hooks/useOrganizationTab';

export function OrganizationTab() {
  const { draft, loading, saving, dirty, patch, save } = useOrganizationTab();

  if (loading) {
    return <PageState message="Loading organization settings…" />;
  }

  const handleSave = () => {
    save().catch((err) => console.error('Save organization failed:', err));
  };

  return (
    <div className="space-y-4">
      <div className="workspace-block">
        <div className="mb-4 flex items-start gap-3">
          <div className="rounded-lg bg-accent p-2 text-primary">
            <Building2 className="h-5 w-5" />
          </div>
          <div>
            <h3 className="text-base font-semibold">Organization</h3>
            <p className="mt-0.5 text-sm text-muted-foreground">
              Set your organization name and industry. These are used as defaults for all agent
              personas — individual agents can override them.
            </p>
          </div>
        </div>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <div>
            <label className="mb-1 block text-sm font-medium" htmlFor="org-name">
              Organization name
            </label>
            <Input
              id="org-name"
              placeholder="e.g. Acme Corp"
              value={draft.name ?? ''}
              onChange={(e) => patch({ name: e.target.value || null })}
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Shown to agents as their company context. Leave blank if not applicable.
            </p>
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium" htmlFor="org-industry">
              Industry
            </label>
            <Input
              id="org-industry"
              placeholder="e.g. Software / Technology"
              value={draft.industry ?? ''}
              onChange={(e) => patch({ industry: e.target.value || null })}
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Helps agents tailor responses to your sector. Leave blank if not applicable.
            </p>
          </div>
        </div>

        <div className="mt-4 flex items-center gap-3">
          <Button onClick={handleSave} disabled={saving || !dirty}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
          {!dirty && !saving && (
            <span className="text-xs text-muted-foreground">No unsaved changes.</span>
          )}
        </div>
      </div>
    </div>
  );
}
