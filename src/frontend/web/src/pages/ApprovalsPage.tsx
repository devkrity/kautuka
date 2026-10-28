import React from 'react';
import {
  useApprovalsPage,
  ApprovalsToolbar,
  PendingApprovalCard,
  ApprovalHistoryCard,
} from '@/features/approvals';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { Alert } from '@/components/ui/alert';
import { ChevronDown } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useProgressiveLimit } from '@/components/layout/useProgressiveLimit';
import { ShowMoreButton } from '@/components/layout/ShowMoreButton';

const isResolvingItem = (resolving: Record<string, boolean>, id: string): boolean => {
  const value = new Map(Object.entries(resolving)).get(id);
  return value === true;
};

export const ApprovalsPage: React.FC = () => {
  const {
    tab,
    scope,
    pending,
    resolving,
    history,
    historyHasMore,
    historyLoading,
    loading,
    error,
    isAdmin,
    onChangeTab,
    onChangeScope,
    resolveApproval,
    loadMore,
  } = useApprovalsPage();
  const {
    visibleItems: visiblePending,
    hiddenCount: hiddenPendingCount,
    showMore: showMorePending,
  } = useProgressiveLimit(pending);

  return (
    <div className="page-shell">
      <PageIntro
        kicker="Human Approval"
        title="Approvals"
        description="Review things waiting for your OK, or browse past decisions."
      />

      <div className="workspace-panel">
        <ApprovalsToolbar
          tab={tab}
          pendingCount={pending.length}
          isAdmin={Boolean(isAdmin)}
          scope={scope}
          onTabChange={onChangeTab}
          onScopeChange={(next) => void onChangeScope(next)}
        />

        {error && (
          <Alert variant="destructive" className="mb-3 px-3 py-2">
            {error}
          </Alert>
        )}

        {tab === 'pending' &&
          (() => {
            if (pending.length === 0) {
              return (
                <div className="rounded-xl border border-dashed border-border bg-muted/30 p-8 text-center text-sm text-muted-foreground">
                  No pending approvals.
                </div>
              );
            }

            return (
              <div className="space-y-3">
                {visiblePending.map((item) => (
                  <PendingApprovalCard
                    key={item.id}
                    item={item}
                    busy={isResolvingItem(resolving, item.id)}
                    onResolve={(requestId, action, reason) =>
                      resolveApproval(requestId, action, reason)
                    }
                  />
                ))}
                <ShowMoreButton count={hiddenPendingCount} onClick={showMorePending} />
              </div>
            );
          })()}

        {tab === 'history' &&
          (() => {
            if (historyLoading && history.length === 0) {
              return <PageState message="Loading history..." />;
            }

            if (history.length === 0) {
              return (
                <div className="rounded-xl border border-dashed border-border bg-muted/30 p-8 text-center text-sm text-muted-foreground">
                  No approval history yet.
                </div>
              );
            }

            return (
              <div className="space-y-3">
                {history.map((item) => (
                  <ApprovalHistoryCard key={item.id} item={item} />
                ))}

                {historyHasMore && (
                  <div className="pt-2 text-center">
                    <Button
                      type="button"
                      disabled={historyLoading}
                      onClick={loadMore}
                      variant="secondary"
                      size="sm"
                    >
                      <ChevronDown className="h-4 w-4" />
                      {historyLoading ? 'Loading...' : 'Load more'}
                    </Button>
                  </div>
                )}
              </div>
            );
          })()}
      </div>
    </div>
  );
};

export default ApprovalsPage;
