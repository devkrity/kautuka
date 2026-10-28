import { PageState } from '@/components/layout/PageState';
import { StatusPanels } from '@/components/layout/StatusPanels';
import { Button } from '@/components/ui/button';
import React from 'react';
import { useUsersTab } from '../hooks/useUsersTab';
import { Banner } from './SettingsShared';
import { UserAccessCard } from './UserAccessCard';
import { UsersFilterCard } from './UsersFilterCard';
import { UsersInviteCard } from './UsersInviteCard';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { Plus } from 'lucide-react';

export function UsersTab() {
  const {
    users,
    agents,
    loading,
    savingUserId,
    editingUserId,
    result,
    setResult,
    search,
    roleFilter,
    statusFilter,
    pageSize,
    totalPages,
    safePage,
    pagedUsers,
    filteredUsers,
    invite,
    getIsAdminDraft,
    getIsActiveDraft,
    getCanImportConnectorsDraft,
    getCanImportSkillsDraft,
    getChannelDeviceId,
    getChannelBotToken,
    getChannelDefaultTarget,
    getChannelHitlTetherMode,
    getUserSaveBlockedReason,
    setSearch,
    setRoleFilter,
    setStatusFilter,
    setPage,
    setPageSize,
    setInvite,
    setIsAdminDraft,
    setIsActiveDraft,
    setCanImportConnectorsDraft,
    setCanImportSkillsDraft,
    setChannelDeviceForUser,
    setChannelBotTokenForUser,
    setChannelDefaultTargetForUser,
    setChannelHitlTetherModeForUser,
    setEditingUserId,
    inviteUser,
    saveUserEdits,
    hydrateUserDraft,
    cancelUserEdits,
    saveChannelSettings,
    dispatchChannelHitl,
    clearExternalLogin,
    statusItems,
  } = useUsersTab();

  const runAsync = (action: () => Promise<unknown>) => {
    action().catch((error) => {
      console.error('Users action failed:', error);
    });
  };
  const [inviteDialogOpen, setInviteDialogOpen] = React.useState(false);

  const renderUserCard = (user: (typeof pagedUsers)[number], editing: boolean) => (
    <UserAccessCard
      key={user.id}
      user={user}
      agents={agents}
      editing={editing}
      saving={savingUserId === user.id}
      isAdminDraft={getIsAdminDraft(user.id)}
      isActiveDraft={getIsActiveDraft(user.id)}
      canImportConnectorsDraft={getCanImportConnectorsDraft(user.id)}
      canImportSkillsDraft={getCanImportSkillsDraft(user.id)}
      channelDeviceId={getChannelDeviceId(user.id)}
      channelBotToken={getChannelBotToken(user.id)}
      channelDefaultTarget={getChannelDefaultTarget(user.id)}
      getChannelHitlTetherMode={(providerKey) => getChannelHitlTetherMode(user.id, providerKey)}
      saveBlockedReason={getUserSaveBlockedReason(user)}
      onStartEdit={() => {
        setResult(null);
        hydrateUserDraft(user);
        setEditingUserId(user.id);
      }}
      onCancelEdit={() => cancelUserEdits(user)}
      onSetIsAdmin={(value) => setIsAdminDraft(user.id, value)}
      onSetIsActive={(value) => setIsActiveDraft(user.id, value)}
      onSetCanImportConnectors={(value) => setCanImportConnectorsDraft(user.id, value)}
      onSetCanImportSkills={(value) => setCanImportSkillsDraft(user.id, value)}
      onChannelDeviceChange={(value) => setChannelDeviceForUser(user.id, value)}
      onChannelBotTokenChange={(value) => setChannelBotTokenForUser(user.id, value)}
      onChannelDefaultTargetChange={(value) => setChannelDefaultTargetForUser(user.id, value)}
      onChannelHitlTetherModeChange={(providerKey, value) => setChannelHitlTetherModeForUser(user.id, providerKey, value)}
      onSaveChannelSettings={() => runAsync(() => saveChannelSettings(user.id))}
      onDispatchChannelHitl={() => runAsync(() => dispatchChannelHitl(user.id))}
      onSave={() => runAsync(() => saveUserEdits(user))}
      onClearExternalLogin={clearExternalLogin}
    />
  );

  let content: React.ReactNode;
  if (loading) {
    content = <PageState message="Loading users..." minHeightClassName="min-h-36" />;
  } else if (filteredUsers.length === 0) {
    content = <PageState message="No users found." minHeightClassName="min-h-36" />;
  } else {
    content = (
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        {pagedUsers.map((user) => renderUserCard(user, false))}
      </div>
    );
  }
  // Keep the edit target independent of the active filters. If the list is
  // filtered while the dialog is open, the edited account must remain mounted.
  const selectedUser = users.find((entry) => entry.id === editingUserId) ?? null;

  return (
    <div className="space-y-4">
      {result && <Banner success={result.success} message={result.message} />}

      <div className="flex justify-end">
        <Button
          type="button"
          onClick={() => {
            setResult(null);
            setInviteDialogOpen(true);
          }}
        >
          <Plus className="mr-2 h-4 w-4" /> Invite user
        </Button>
      </div>

      <Dialog open={inviteDialogOpen} onOpenChange={setInviteDialogOpen}>
        <DialogContent className="max-h-[90vh] overflow-y-auto p-4 sm:max-w-2xl sm:p-6">
          <DialogTitle className="sr-only">Invite a user</DialogTitle>
          <DialogDescription className="sr-only">
            Create access for a new Kautuka user.
          </DialogDescription>
          {result && <Banner success={result.success} message={result.message} />}
          <UsersInviteCard
            invite={invite}
            onInviteChange={setInvite}
            onInvite={() => {
              void inviteUser().then((success) => {
                if (success) setInviteDialogOpen(false);
              });
            }}
          />
        </DialogContent>
      </Dialog>

      <Dialog
        open={editingUserId !== null}
        onOpenChange={(open) => {
          if (!open && editingUserId) {
            const user = users.find((entry) => entry.id === editingUserId);
            if (user) cancelUserEdits(user);
            else setEditingUserId(null);
          }
        }}
      >
        <DialogContent className="max-h-[90vh] overflow-y-auto p-4 sm:max-w-3xl sm:p-6">
          <DialogTitle className="sr-only">
            Edit access for {selectedUser?.username ?? 'user'}
          </DialogTitle>
          <DialogDescription className="sr-only">
            Manage account access, import permissions, and Telegram integration settings.
          </DialogDescription>
          {result && <Banner success={result.success} message={result.message} />}
          {selectedUser ? renderUserCard(selectedUser, true) : null}
        </DialogContent>
      </Dialog>

      <UsersFilterCard
        search={search}
        roleFilter={roleFilter}
        statusFilter={statusFilter}
        pageSize={pageSize}
        shownCount={pagedUsers.length}
        totalCount={filteredUsers.length}
        onSearchChange={setSearch}
        onRoleFilterChange={setRoleFilter}
        onStatusFilterChange={setStatusFilter}
        onPageSizeChange={setPageSize}
      />

      {content}

      {!loading && filteredUsers.length > 0 && (
        <div className="workspace-block">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <p className="text-sm text-muted-foreground">
              Page {safePage} of {totalPages}
            </p>
            <div className="flex items-center gap-2">
              <Button
                variant="secondary"
                onClick={() => setPage(Math.max(1, safePage - 1))}
                disabled={safePage <= 1}
              >
                Previous
              </Button>
              <Button
                variant="secondary"
                onClick={() => setPage(Math.min(totalPages, safePage + 1))}
                disabled={safePage >= totalPages}
              >
                Next
              </Button>
            </div>
          </div>
        </div>
      )}

      <StatusPanels items={statusItems} />
    </div>
  );
}
