import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { useProgressiveLimit } from '@/components/layout/useProgressiveLimit';
import { ShowMoreButton } from '@/components/layout/ShowMoreButton';
import { useCurrentUser } from '@/features/auth';
import {
  ConnectorCard,
  ConnectorFormModal,
  ImportModal,
  blankDraft,
  buildSavePayload,
  entryToDraft,
  useCompleteConnectorOAuth,
  useConnectors,
  useDeleteConnector,
  useSaveConnector,
  useSaveConnectorCredentials,
  useSaveConnectorDiscovery,
  type Connector,
  type ConnectorDraft,
} from '@/features/connectors';
import {
  CatalogRecommendationCard,
  ROLE_ADMIN,
  useCatalogRecommendations,
  useInstallCatalogRecommendation,
} from '@/features/settings';
import { useQueryClient } from '@tanstack/react-query';
import { Plug, Plus, Settings2, Sparkles, Upload } from 'lucide-react';
import React, { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';

function getModalDraft(modalTarget: 'new' | Connector | null): ConnectorDraft | null {
  if (modalTarget === 'new') {
    return blankDraft();
  }

  if (modalTarget) {
    return entryToDraft(modalTarget);
  }

  return null;
}

function getErrorMessage(error: unknown): string | null {
  if (error instanceof Error) {
    return error.message;
  }

  if (typeof error === 'string') {
    return error;
  }

  return error ? 'An unexpected error occurred.' : null;
}

export const ConnectorsPage: React.FC = () => {
  const { data: currentUser } = useCurrentUser();
  const { data: connectors = [], isLoading, error } = useConnectors();
  const saveMutation = useSaveConnector();
  const discoveryMutation = useSaveConnectorDiscovery();
  const credsMutation = useSaveConnectorCredentials();
  const deleteMutation = useDeleteConnector();
  const queryClient = useQueryClient();
  const recommendationsQuery = useCatalogRecommendations();
  const installRecommendationMutation = useInstallCatalogRecommendation('connectors');

  const completeOAuth = useCompleteConnectorOAuth();
  const completeOAuthRef = useRef(completeOAuth.mutate);
  completeOAuthRef.current = completeOAuth.mutate;
  const [togglingId, setTogglingId] = useState<string | null>(null);

  useEffect(() => {
    const hash = window.location.hash.startsWith('#')
      ? window.location.hash.slice(1)
      : window.location.hash;
    if (!hash) {
      return;
    }

    const params = new URLSearchParams(hash);
    const code = params.get('code');
    const brokerTicket = params.get('broker_ticket');
    const state = params.get('state');
    const signedIn = params.get('signed_in');
    const error = params.get('error_description') ?? params.get('error');
    if (!code && !brokerTicket && !error && signedIn !== '1') {
      return;
    }

    if (signedIn === '1' && !code && !error) {
      window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}`);
      toast.success('Signed in.');
      return;
    }

    window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}`);
    if (error || !code || !state) {
      toast.error(error ?? 'Microsoft did not complete sign-in.');
      return;
    }

    completeOAuthRef.current(
      { code, brokerTicket: brokerTicket ?? undefined, state },
      {
        onSuccess: (result) => {
          if (result.status === 'connected') {
            toast.success(result.message);
            return;
          }
          toast.error(result.message);
        },
        onError: () => toast.error('Microsoft sign-in could not be finished.'),
      }
    );
  }, []);
  const [modalTarget, setModalTarget] = useState<'new' | Connector | null>(null);
  const [importOpen, setImportOpen] = useState(false);

  const canManageConnector = (connector: Connector | null | undefined): boolean => {
    if (!connector || !currentUser) {
      return false;
    }

    if (currentUser.role === ROLE_ADMIN) {
      return true;
    }

    return connector.scope === 'user' && connector.ownerUserId === currentUser.id;
  };

  const canImportConnectors =
    !!currentUser && (currentUser.role === ROLE_ADMIN || Boolean(currentUser.canImportConnectors));

  const handleToggle = (id: string, enabled: boolean) => {
    const current = connectors.find((c) => c.id === id);
    if (!current) return;
    if (!canManageConnector(current)) {
      toast.error('Only admins can change system connector settings.');
      return;
    }
    setTogglingId(id);
    saveMutation.mutate(
      { connectorId: id, data: buildSavePayload({ ...entryToDraft(current), enabled }) },
      { onSettled: () => setTogglingId(null) }
    );
  };

  const handleSave = (d: ConnectorDraft, credValues: Record<string, string>) => {
    const current = connectors.find((connector) => connector.id === d.id);
    const nonEmpty = Object.fromEntries(
      Object.entries(credValues).filter(([, v]) => v.trim().length > 0)
    );
    const httpUrlOverride =
      d.transport === 'http' && d.httpUrl.trim().length > 0 ? d.httpUrl : null;

    if (current && !canManageConnector(current)) {
      if (Object.keys(nonEmpty).length === 0 && !httpUrlOverride) {
        toast.error(
          'Only admins can change system connector settings. Add credentials or a personal HTTP URL to save personal access.'
        );
        return;
      }

      credsMutation.mutate(
        { connectorId: d.id, entries: nonEmpty, httpUrl: httpUrlOverride },
        {
          onSuccess: () => {
            setModalTarget(null);
            toast.success('Personal connector access saved.');
          },
        }
      );
      return;
    }

    saveMutation.mutate(
      { connectorId: d.id, data: buildSavePayload(d) },
      {
        onSuccess: () => {
          setModalTarget(null);
          if (Object.keys(nonEmpty).length > 0) {
            credsMutation.mutate({
              connectorId: d.id,
              entries: nonEmpty,
              httpUrl: httpUrlOverride,
            });
          }
        },
      }
    );
  };

  const handleDelete = (connector: Connector) => {
    if (!canManageConnector(connector)) {
      toast.error('Only admins can delete system connectors.');
      return;
    }
    if (!globalThis.confirm(`Delete connector "${connector.name}"? This cannot be undone.`)) return;
    deleteMutation.mutate(connector.id);
  };

  const handleToolCountSaved = (
    connectorId: string,
    toolCount: number,
    toolNames: string[] | null
  ) => {
    discoveryMutation.mutate({
      connectorId,
      toolCount,
      toolNames,
    });
  };

  const enabled = connectors.filter((c) => c.enabled);
  const disabled = connectors.filter((c) => !c.enabled);
  const existingIds = new Set(connectors.map((c) => c.id));
  const recommendedConnectors = recommendationsQuery.data?.connectors ?? [];
  const visibleRecommended = useProgressiveLimit(recommendedConnectors);
  const visibleEnabled = useProgressiveLimit(enabled);
  const visibleDisabled = useProgressiveLimit(disabled);
  const recommendationsMessage = recommendationsQuery.isError
    ? 'Hosted catalog is currently unreachable. Check again later.'
    : (recommendationsQuery.data?.message ?? null);
  const installingRecommendationKey = installRecommendationMutation.isPending
    ? `${installRecommendationMutation.variables.kind}:${installRecommendationMutation.variables.id}`
    : null;

  const modalDraft = getModalDraft(modalTarget);
  const errorMsg = getErrorMessage(error);

  return (
    <div className="page-shell">
      {importOpen && canImportConnectors && (
        <ImportModal
          existingIds={existingIds}
          onImported={() => {
            queryClient.invalidateQueries({ queryKey: ['connectors'] });
          }}
          onClose={() => setImportOpen(false)}
        />
      )}
      {modalDraft != null && (
        <ConnectorFormModal
          key={modalTarget != null && modalTarget !== 'new' ? modalTarget.id : 'new'}
          draft={modalDraft}
          isNew={modalTarget === 'new'}
          onSave={handleSave}
          onClose={() => setModalTarget(null)}
          saving={saveMutation.isPending || credsMutation.isPending}
          saveLabel={
            modalTarget !== 'new' && modalTarget != null && !canManageConnector(modalTarget)
              ? 'Save Credentials'
              : undefined
          }
          onToolCountSaved={
            modalTarget != null && modalTarget !== 'new'
              ? (count, names) => handleToolCountSaved(modalTarget.id, count, names)
              : undefined
          }
        />
      )}
      <PageIntro
        kicker="Integrations"
        title="Connectors"
        description="Enable tool integrations for your agents. Assign connectors in agent settings."
        actions={
          <div className="flex items-center gap-3">
            <span className="text-xs text-muted-foreground flex items-center gap-1.5">
              <Settings2 className="h-4 w-4" />
              {enabled.length} of {connectors.length} active
            </span>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => {
                if (!canImportConnectors) {
                  toast.error('You do not have permission to import connectors.');
                  return;
                }

                setImportOpen(true);
              }}
              disabled={!canImportConnectors}
            >
              <Upload className="h-4 w-4" /> Import
            </Button>
            <Button
              type="button"
              size="sm"
              onClick={() => {
                if (!canImportConnectors) {
                  toast.error('You do not have permission to import connectors.');
                  return;
                }

                setModalTarget('new');
              }}
              disabled={!canImportConnectors}
            >
              <Plus className="h-4 w-4" /> Advanced Connector Import
            </Button>
          </div>
        }
      />

      {!recommendationsQuery.isLoading && (
        <section className="space-y-3">
          <div className="flex items-start justify-between gap-3">
            <div>
              <h2 className="section-label">Recommended</h2>
              <p className="mt-1 text-sm text-muted-foreground">
                Catalog-backed connector recommendations you can install on demand. Experimental;
                the hosted catalog may be unreachable.
              </p>
            </div>
            <span className="inline-flex items-center gap-1 rounded-full border border-border bg-card px-3 py-1 text-xs text-muted-foreground">
              <Sparkles className="h-3.5 w-3.5" /> Hosted catalog (experimental)
            </span>
          </div>
          {recommendationsMessage ? (
            <div className="rounded-xl border border-border bg-muted/40 p-4 text-sm text-muted-foreground">
              {recommendationsMessage}
            </div>
          ) : null}
          {recommendedConnectors.length > 0 ? (
            <>
              <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {visibleRecommended.visibleItems.map((recommendation) => {
                  const isInstalling =
                    installingRecommendationKey === `${recommendation.kind}:${recommendation.id}`;

                  return (
                    <CatalogRecommendationCard
                      key={recommendation.id}
                      recommendation={recommendation}
                      icon="connector"
                      canImport={canImportConnectors}
                      isInstalling={isInstalling}
                      onInstall={(item) => installRecommendationMutation.mutate(item)}
                    />
                  );
                })}
              </div>
              <ShowMoreButton
                count={visibleRecommended.hiddenCount}
                onClick={visibleRecommended.showMore}
              />
            </>
          ) : (
            <div className="rounded-xl border border-dashed border-border p-5 text-sm text-muted-foreground">
              No connector recommendations are available from the hosted catalog yet.
            </div>
          )}
        </section>
      )}

      {isLoading && <PageState message="Loading connectors…" />}
      {!isLoading && errorMsg && <Alert variant="destructive">{errorMsg}</Alert>}

      {!isLoading && !errorMsg && connectors.length === 0 && (
        <div className="flex flex-col items-center justify-center gap-2 py-20 text-muted-foreground">
          <Plug className="h-10 w-10 opacity-30" />
          <p className="text-sm">No connectors configured yet.</p>
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="mt-2"
            onClick={() => {
              if (!canImportConnectors) {
                toast.error('You do not have permission to import connectors.');
                return;
              }

              setModalTarget('new');
            }}
            disabled={!canImportConnectors}
          >
            <Plus className="h-4 w-4" /> Start advanced connector import
          </Button>
        </div>
      )}

      {!isLoading && !errorMsg && connectors.length > 0 && (
        <div className="space-y-6">
          {enabled.length > 0 && (
            <section>
              <h2 className="mb-3 section-label">Active</h2>
              <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {visibleEnabled.visibleItems.map((c) => (
                  <ConnectorCard
                    key={c.id}
                    connector={c}
                    canManage={canManageConnector(c)}
                    onToggle={handleToggle}
                    onEdit={(entry) => setModalTarget(entry)}
                    onDelete={handleDelete}
                    toggling={togglingId === c.id || deleteMutation.isPending}
                  />
                ))}
              </div>
              <ShowMoreButton
                count={visibleEnabled.hiddenCount}
                onClick={visibleEnabled.showMore}
              />
            </section>
          )}
          {disabled.length > 0 && (
            <section>
              <h2 className="mb-3 section-label">Available</h2>
              <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {visibleDisabled.visibleItems.map((c) => (
                  <ConnectorCard
                    key={c.id}
                    connector={c}
                    canManage={canManageConnector(c)}
                    onToggle={handleToggle}
                    onEdit={(entry) => setModalTarget(entry)}
                    onDelete={handleDelete}
                    toggling={togglingId === c.id || deleteMutation.isPending}
                  />
                ))}
              </div>
              <ShowMoreButton
                count={visibleDisabled.hiddenCount}
                onClick={visibleDisabled.showMore}
              />
            </section>
          )}
        </div>
      )}
    </div>
  );
};
