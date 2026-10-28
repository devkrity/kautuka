import { useEffect, useState } from 'react';
import { toast } from 'sonner';
import { settingsAdmin } from '../api/settingsAdmin';
import { apiError } from '../utils/settingsUtils';
import type { OrganizationSettingsEntry } from '../types/settingsTypes';

export function useOrganizationTab() {
  const [organization, setOrganization] = useState<OrganizationSettingsEntry>({
    name: null,
    industry: null,
  });
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [draft, setDraft] = useState<OrganizationSettingsEntry>({ name: null, industry: null });
  const [dirty, setDirty] = useState(false);

  const load = async () => {
    setLoading(true);
    try {
      const res = await settingsAdmin.getOrganization();
      const org = res.data.organization;
      setOrganization(org);
      setDraft(org);
    } catch (err) {
      toast.error(apiError(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const patch = (updates: Partial<OrganizationSettingsEntry>) => {
    setDraft((prev) => ({ ...prev, ...updates }));
    setDirty(true);
  };

  const save = async () => {
    setSaving(true);
    try {
      const res = await settingsAdmin.saveOrganization({
        name: draft.name?.trim() || null,
        industry: draft.industry?.trim() || null,
      });
      const org = res.data.organization;
      setOrganization(org);
      setDraft(org);
      setDirty(false);
      toast.success('Organization settings saved.');
    } catch (err) {
      toast.error(apiError(err));
    } finally {
      setSaving(false);
    }
  };

  return { organization, draft, loading, saving, dirty, patch, save };
}
