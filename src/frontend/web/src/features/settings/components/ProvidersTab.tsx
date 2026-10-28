import React from 'react';
import { AddEntityTile } from '@/components/layout/AddEntityTile';
import { PageState } from '@/components/layout/PageState';
import { StatusPanels } from '@/components/layout/StatusPanels';
import { Banner } from './SettingsShared';
import { ProviderForm } from './ProviderForm';
import { ProviderCard } from './ProviderCard';
import { useProvidersTab } from '../hooks/useProvidersTab';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { useProgressiveLimit } from '@/components/layout/useProgressiveLimit';
import { ShowMoreButton } from '@/components/layout/ShowMoreButton';

export function ProvidersTab() {
  const {
    providers,
    loading,
    editingId,
    form,
    result,
    saving,
    statusItems,
    setForm,
    startCreate,
    startEdit,
    cancelEdit,
    save,
    remove,
  } = useProvidersTab();

  const runAsync = (action: () => Promise<unknown>) => {
    action().catch((error) => {
      console.error('Providers action failed:', error);
    });
  };
  const { visibleItems: visibleProviders, hiddenCount, showMore } = useProgressiveLimit(providers);

  let content: React.ReactNode;
  if (loading) {
    content = <PageState message="Loading providers..." minHeightClassName="min-h-36" />;
  } else {
    content = (
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        <AddEntityTile
          title="Add Provider"
          description="Integrate a new model host into your orchestration workspace."
          onClick={startCreate}
        />

        {visibleProviders.map((provider) => (
          <ProviderCard
            key={provider.id}
            provider={provider}
            onEdit={startEdit}
            onDelete={(id) => {
              runAsync(() => remove(id));
            }}
          />
        ))}
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {result && <Banner success={result.success} message={result.message} />}

      <Dialog
        open={editingId !== null}
        onOpenChange={(open) => {
          if (!open && !saving) cancelEdit();
        }}
      >
        <DialogContent className="flex max-h-[88vh] flex-col overflow-hidden p-0 sm:max-w-2xl">
          <DialogTitle className="sr-only">
            {editingId ? `Edit provider ${editingId}` : 'Add provider'}
          </DialogTitle>
          <DialogDescription className="sr-only">
            Configure the provider connection details.
          </DialogDescription>
          <div className="min-h-0 overflow-y-auto p-4 sm:p-6">
            {result && <Banner success={result.success} message={result.message} />}
            {editingId !== null && (
              <ProviderForm
                editingId={editingId}
                form={form}
                saving={saving}
                onChange={setForm}
                onCancel={cancelEdit}
                onSave={() => {
                  runAsync(save);
                }}
              />
            )}
          </div>
        </DialogContent>
      </Dialog>

      {content}
      {!loading && <ShowMoreButton count={hiddenCount} onClick={showMore} />}

      <StatusPanels items={statusItems} />
    </div>
  );
}
