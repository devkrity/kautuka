import React from 'react';
import { useSearchParams } from 'react-router-dom';
import { isAxiosError } from 'axios';
import { RefreshCw, Search } from 'lucide-react';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useCurrentUser } from '@/features/auth';
import {
  useNetworkRequests,
  useNetworkRules,
  useNetworkActivity,
  useImageCompat,
  useDecideRequest,
  useCreateOrUpdateRule,
  useDeleteRule,
  useUpdateUserBrake,
  useUpdateSystemBrake,
  useApplyCompatMode,
  useRemoveCompatMode,
  type RequestDecisionDto,
  type NetworkAccessRuleDto,
  type CreateCompatProfileDto,
  GroupedPendingCards,
  RulesList,
  NetworkActivity,
  EmergencyBrakeControls,
  CompatibilityModeCard,
  CompatibilityModeList,
  OpenInternetGrantDialog,
  UserNetworkPrivilegesForm,
} from '@/features/network-access';

type ViewTab = 'requests' | 'rules' | 'activity' | 'compatibility' | 'privileges';

export function NetworkAccessPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const userIdParam = searchParams.get('userId') ?? undefined;
  const tabParam = (searchParams.get('tab') as ViewTab) ?? 'requests';

  const { data: currentUser } = useCurrentUser();
  const isAdmin = currentUser?.role?.toLowerCase() === 'admin';

  const [activeTab, setActiveTab] = React.useState<ViewTab>(tabParam);
  const [search, setSearch] = React.useState('');
  const [generalError, setGeneralError] = React.useState<string | null>(null);

  // Open Internet dialog state
  const [openInternetTarget, setOpenInternetTarget] = React.useState<{
    connectorId: string;
    connectorDisplayName: string;
    userId?: string;
    userDisplayName?: string;
  } | null>(null);

  // Queries
  const requestsQuery = useNetworkRequests();
  const rulesQuery = useNetworkRules(userIdParam);
  const activityQuery = useNetworkActivity();
  const compatQuery = useImageCompat(isAdmin);

  // Mutations
  const decideMutation = useDecideRequest();
  const ruleMutation = useCreateOrUpdateRule();
  const deleteRuleMutation = useDeleteRule();
  const userBrakeMutation = useUpdateUserBrake();
  const systemBrakeMutation = useUpdateSystemBrake();
  const applyCompatMutation = useApplyCompatMode();
  const removeCompatMutation = useRemoveCompatMode();

  const revision = requestsQuery.data?.revision ?? rulesQuery.data?.revision ?? 0;
  const requests = requestsQuery.data?.requests ?? [];
  const privileges = requestsQuery.data?.privileges ?? {
    canApproveOwn: false,
    canAllowAllAgents: false,
  };
  const brake = requestsQuery.data?.brake;

  const pendingRequests = requests.filter((r) => r.status === 'pending');
  const rules = rulesQuery.data?.rules ?? [];
  const activityEntries = activityQuery.data?.entries ?? [];
  const compatPending = compatQuery.data?.pendingRequests ?? [];
  const compatProfiles = compatQuery.data?.profiles ?? [];

  // Filter requests by search
  const filteredRequests = React.useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return pendingRequests;
    return pendingRequests.filter(
      (r) =>
        r.destination.toLowerCase().includes(q) ||
        (r.userDisplayName && r.userDisplayName.toLowerCase().includes(q)) ||
        (r.agentDisplayName && r.agentDisplayName.toLowerCase().includes(q)) ||
        (r.connectorDisplayName && r.connectorDisplayName.toLowerCase().includes(q))
    );
  }, [pendingRequests, search]);

  // Filter rules by search
  const filteredRules = React.useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return rules;
    return rules.filter(
      (r) =>
        r.destination.toLowerCase().includes(q) ||
        (r.userDisplayName && r.userDisplayName.toLowerCase().includes(q)) ||
        (r.agentDisplayName && r.agentDisplayName.toLowerCase().includes(q)) ||
        (r.connectorDisplayName && r.connectorDisplayName.toLowerCase().includes(q)) ||
        (r.summaryText && r.summaryText.toLowerCase().includes(q))
    );
  }, [rules, search]);

  // Error mapper
  const mapApiError = (err: unknown): string => {
    if (isAxiosError(err)) {
      const status = err.response?.status;
      if (status === 409) {
        return 'The policy changed while you were editing. The latest policy has been loaded; review it and try again.';
      }
      if (status === 403) {
        return 'You do not have permission to approve this destination. Ask an administrator to review it.';
      }
      if (status === 400) {
        return 'This destination is not permitted or is invalid. Use an exact hostname or IPv4 address and a TCP port from 1 to 65535.';
      }
    }
    return 'The change could not be saved or applied. Refresh to check its current status before retrying.';
  };

  // Decisions
  const handleDecide = async (decision: RequestDecisionDto, requestId: string) => {
    setGeneralError(null);
    try {
      await decideMutation.mutateAsync({
        requestId,
        decision,
      });
      await requestsQuery.refetch();
      await rulesQuery.refetch();
    } catch (err) {
      setGeneralError(mapApiError(err));
      await requestsQuery.refetch();
    }
  };

  const handleDeleteRule = async (ruleId: string, expectedRevision: number) => {
    setGeneralError(null);
    try {
      await deleteRuleMutation.mutateAsync({
        ruleId,
        expectedRevision,
      });
      await rulesQuery.refetch();
    } catch (err) {
      setGeneralError(mapApiError(err));
      await rulesQuery.refetch();
    }
  };

  const handleBlockSeedRule = async (rule: NetworkAccessRuleDto) => {
    setGeneralError(null);
    try {
      await ruleMutation.mutateAsync({
        scopeKind: rule.scopeKind,
        userId: rule.userId,
        agentId: rule.agentId,
        connectorId: rule.connectorId,
        destination: rule.destination,
        destKind: rule.destKind,
        port: rule.port,
        allowed: false,
        expectedRevision: revision,
      });
      await rulesQuery.refetch();
    } catch (err) {
      setGeneralError(mapApiError(err));
      await rulesQuery.refetch();
    }
  };

  const handleApplyCompat = async (digest: string, profile: CreateCompatProfileDto) => {
    setGeneralError(null);
    try {
      await applyCompatMutation.mutateAsync({
        imageDigest: digest,
        profile,
      });
      await compatQuery.refetch();
    } catch (err) {
      setGeneralError(mapApiError(err));
      await compatQuery.refetch();
    }
  };

  const handleRemoveCompat = async (digest: string) => {
    setGeneralError(null);
    try {
      await removeCompatMutation.mutateAsync(digest);
      await compatQuery.refetch();
    } catch (err) {
      setGeneralError(mapApiError(err));
      await compatQuery.refetch();
    }
  };

  const handleGrantOpenInternet = async () => {
    if (!openInternetTarget) return;
    setGeneralError(null);
    try {
      await ruleMutation.mutateAsync({
        scopeKind: 'user',
        userId: openInternetTarget.userId,
        connectorId: openInternetTarget.connectorId,
        destination: '',
        destKind: 'any-public',
        port: 0,
        allowed: true,
        expectedRevision: revision,
      });
      await rulesQuery.refetch();
      setOpenInternetTarget(null);
    } catch (err) {
      setGeneralError(mapApiError(err));
    }
  };

  const handleTabChange = (nextTab: ViewTab) => {
    setActiveTab(nextTab);
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.set('tab', nextTab);
      return next;
    });
  };

  const handleRefresh = async () => {
    setGeneralError(null);
    await Promise.all([
      requestsQuery.refetch(),
      rulesQuery.refetch(),
      activityQuery.refetch(),
      isAdmin ? compatQuery.refetch() : Promise.resolve(),
    ]);
  };

  if (requestsQuery.isLoading && !requestsQuery.data) {
    return <PageState message="Loading network access..." />;
  }

  return (
    <div className="w-full max-w-full overflow-x-hidden space-y-6">
      <PageIntro
        kicker="Security & Connectivity"
        title="Network Access"
        description="Review outbound network requests, manage rules, and control agent connectivity."
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <EmergencyBrakeControls
              brake={brake}
              currentUser={currentUser}
              onUpdateUserBrake={async (on) => {
                await userBrakeMutation.mutateAsync({ on });
                await requestsQuery.refetch();
              }}
              onUpdateSystemBrake={
                isAdmin
                  ? async (on) => {
                      await systemBrakeMutation.mutateAsync({ on });
                      await requestsQuery.refetch();
                    }
                  : undefined
              }
            />
            <Button
              type="button"
              variant="ghost"
              size="icon"
              title="Refresh network policy"
              onClick={() => void handleRefresh()}
              disabled={requestsQuery.isFetching}
            >
              <RefreshCw
                className={`h-4 w-4 ${requestsQuery.isFetching ? 'animate-spin' : ''}`}
              />
            </Button>
          </div>
        }
      />

      {generalError && (
        <Alert variant="destructive" className="text-sm">
          {generalError}
        </Alert>
      )}

      {userIdParam && (
        <Alert variant="default" className="text-sm flex items-center justify-between">
          <span>Filtering rules for user ID: {userIdParam}</span>
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => {
              setSearchParams((prev) => {
                const next = new URLSearchParams(prev);
                next.delete('userId');
                return next;
              });
            }}
          >
            Clear filter
          </Button>
        </Alert>
      )}

      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-1.5 border-b border-border pb-1">
          <Button
            type="button"
            variant={activeTab === 'requests' ? 'default' : 'ghost'}
            size="sm"
            onClick={() => handleTabChange('requests')}
            className="text-xs sm:text-sm font-semibold"
          >
            Requests ({pendingRequests.length})
          </Button>
          <Button
            type="button"
            variant={activeTab === 'rules' ? 'default' : 'ghost'}
            size="sm"
            onClick={() => handleTabChange('rules')}
            className="text-xs sm:text-sm font-semibold"
          >
            Rules ({rules.length})
          </Button>
          <Button
            type="button"
            variant={activeTab === 'activity' ? 'default' : 'ghost'}
            size="sm"
            onClick={() => handleTabChange('activity')}
            className="text-xs sm:text-sm font-semibold"
          >
            Activity
          </Button>
          {isAdmin && (
            <>
              <Button
                type="button"
                variant={activeTab === 'compatibility' ? 'default' : 'ghost'}
                size="sm"
                onClick={() => handleTabChange('compatibility')}
                className="text-xs sm:text-sm font-semibold"
              >
                Compatibility ({compatProfiles.length})
              </Button>
              <Button
                type="button"
                variant={activeTab === 'privileges' ? 'default' : 'ghost'}
                size="sm"
                onClick={() => handleTabChange('privileges')}
                className="text-xs sm:text-sm font-semibold"
              >
                Privileges
              </Button>
            </>
          )}
        </div>

        <div className="relative w-full sm:w-64">
          <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
          <Input
            type="search"
            placeholder="Search..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="pl-9 h-9 text-xs sm:text-sm"
          />
        </div>
      </div>

      {/* Tab: Requests */}
      {activeTab === 'requests' && (
        <section className="space-y-4" aria-labelledby="requests-tab-heading">
          <h2 id="requests-tab-heading" className="sr-only">
            Pending network requests
          </h2>

          {/* Compatibility Requests (Admin only) */}
          {isAdmin && compatPending.length > 0 && (
            <div className="space-y-3 pb-4 border-b border-border">
              <h3 className="font-semibold text-base text-foreground">
                Container compatibility requests ({compatPending.length})
              </h3>
              {compatPending.map((req) => (
                <CompatibilityModeCard
                  key={req.imageDigest}
                  request={req}
                  onApply={(profile) => handleApplyCompat(req.imageDigest, profile)}
                />
              ))}
            </div>
          )}

          {filteredRequests.length === 0 ? (
            <div className="rounded-xl border border-dashed border-border p-8 text-center">
              <p className="text-sm text-muted-foreground">No pending network requests.</p>
            </div>
          ) : (
            <GroupedPendingCards
              requests={filteredRequests}
              currentUser={currentUser}
              userPrivileges={privileges}
              expectedRevision={revision}
              onDecide={handleDecide}
            />
          )}
        </section>
      )}

      {/* Tab: Rules */}
      {activeTab === 'rules' && (
        <section className="space-y-4" aria-labelledby="rules-tab-heading">
          <h2 id="rules-tab-heading" className="sr-only">
            Network rules
          </h2>
          <RulesList
            rules={filteredRules}
            expectedRevision={revision}
            currentUser={currentUser}
            onDeleteRule={handleDeleteRule}
            onBlockSeedRule={isAdmin ? handleBlockSeedRule : undefined}
          />
        </section>
      )}

      {/* Tab: Activity */}
      {activeTab === 'activity' && (
        <section className="space-y-4" aria-labelledby="activity-tab-heading">
          <h2 id="activity-tab-heading" className="sr-only">
            Network activity
          </h2>
          <NetworkActivity entries={activityEntries} search={search} />
        </section>
      )}

      {/* Tab: Compatibility (Admin only) */}
      {isAdmin && activeTab === 'compatibility' && (
        <section className="space-y-6" aria-labelledby="compat-tab-heading">
          <h2 id="compat-tab-heading" className="sr-only">
            Compatibility mode
          </h2>

          {compatPending.length > 0 && (
            <div className="space-y-3">
              <h3 className="font-semibold text-base">Pending requests ({compatPending.length})</h3>
              {compatPending.map((req) => (
                <CompatibilityModeCard
                  key={req.imageDigest}
                  request={req}
                  onApply={(profile) => handleApplyCompat(req.imageDigest, profile)}
                />
              ))}
            </div>
          )}

          <CompatibilityModeList
            profiles={compatProfiles}
            onRemove={handleRemoveCompat}
          />
        </section>
      )}

      {/* Tab: Privileges (Admin only) */}
      {isAdmin && activeTab === 'privileges' && (
        <section className="space-y-4 max-w-xl" aria-labelledby="privileges-tab-heading">
          <h2 id="privileges-tab-heading" className="sr-only">
            User network privileges
          </h2>
          <p className="text-sm text-muted-foreground">
            Configure default network access and approval privileges for users.
          </p>
          <UserNetworkPrivilegesForm
            userId={currentUser?.id}
            userDisplayName={currentUser?.displayName || 'Administrator'}
            canApproveOwn={privileges.canApproveOwn}
            canAllowAllAgents={privileges.canAllowAllAgents}
            isAdminViewer={true}
          />
        </section>
      )}

      {/* Open Internet Grant Dialog */}
      {openInternetTarget && (
        <OpenInternetGrantDialog
          open={Boolean(openInternetTarget)}
          onOpenChange={(open) => !open && setOpenInternetTarget(null)}
          connectorDisplayName={openInternetTarget.connectorDisplayName}
          userDisplayName={openInternetTarget.userDisplayName}
          onConfirm={handleGrantOpenInternet}
        />
      )}
    </div>
  );
}

export default NetworkAccessPage;
