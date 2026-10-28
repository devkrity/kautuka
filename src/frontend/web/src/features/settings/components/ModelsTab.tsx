import React from 'react';
import { AddEntityTile } from '@/components/layout/AddEntityTile';
import { PageState } from '@/components/layout/PageState';
import { StatusPanels } from '@/components/layout/StatusPanels';
import { Banner } from './SettingsShared';
import { ModelForm } from './ModelForm';
import { ModelCard } from './ModelCard';
import { useModelsTab } from '../hooks/useModelsTab';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { useProgressiveLimit } from '@/components/layout/useProgressiveLimit';
import { ShowMoreButton } from '@/components/layout/ShowMoreButton';

export function ModelsTab() {
  const {
    models,
    providers,
    availableModelNames,
    discoveringModels,
    loading,
    editingId,
    form,
    formCapabilities,
    result,
    saving,
    probingId,
    selectedProviderType,
    statusItems,
    setForm,
    setFormCapabilities,
    startCreate,
    startEdit,
    cancelEdit,
    save,
    remove,
    probeModel,
    discoverModelNames,
    providerLabel,
  } = useModelsTab();

  const runAsync = (action: () => Promise<unknown>) => {
    action().catch((error) => {
      console.error('Models action failed:', error);
    });
  };
  const { visibleItems: visibleModels, hiddenCount, showMore } = useProgressiveLimit(models);

  let content: React.ReactNode;
  if (loading) {
    content = <PageState message="Loading models..." minHeightClassName="min-h-36" />;
  } else {
    content = (
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        <AddEntityTile
          title="Add Model"
          description="Create a provider-bound model entry for agent runtime."
          onClick={startCreate}
        />

        {visibleModels.map((model) => (
          <ModelCard
            key={model.id}
            model={model}
            probingId={probingId}
            providerLabel={providerLabel}
            onProbe={(id) => {
              runAsync(() => probeModel(id));
            }}
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
        <DialogContent className="flex max-h-[88vh] flex-col overflow-hidden p-0 sm:max-w-3xl">
          <DialogTitle className="sr-only">
            {editingId ? `Edit model ${editingId}` : 'Add model'}
          </DialogTitle>
          <DialogDescription className="sr-only">
            Choose a provider and configure its model.
          </DialogDescription>
          <div className="min-h-0 overflow-y-auto p-4 sm:p-6">
            {result && <Banner success={result.success} message={result.message} />}
            {editingId !== null && (
              <ModelForm
                editingId={editingId}
                form={form}
                providers={providers}
                availableModelNames={availableModelNames}
                discoveringModels={discoveringModels}
                formCapabilities={formCapabilities}
                selectedProviderType={selectedProviderType}
                saving={saving}
                probingId={probingId}
                onChange={setForm}
                onSetCapabilities={setFormCapabilities}
                onCancel={cancelEdit}
                onSave={() => {
                  runAsync(save);
                }}
                onProbe={(id) => {
                  runAsync(() => probeModel(id));
                }}
                onDiscover={(providerId, currentModelName) => {
                  runAsync(() => discoverModelNames(providerId, currentModelName));
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
