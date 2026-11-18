import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { settingsAdmin } from '@/features/settings';
import { useUpdateUserPrivileges, useUserPrivileges } from '../networkAccess';
import { UserNetworkPrivilegesForm } from './UserNetworkPrivilegesForm';

export function NetworkPrivilegesPanel() {
  const [userId, setUserId] = React.useState('');
  const users = useQuery({ queryKey: ['network-access', 'users'], queryFn: async () => (await settingsAdmin.listUsers()).data.users });
  const privileges = useUserPrivileges(userId || undefined);
  const update = useUpdateUserPrivileges();
  return <div className="space-y-4">
    {users.isError ? <p role="alert">Could not load users. Refresh and try again.</p> : <label className="block">User
      <select aria-label="User" className="block w-full rounded border p-2" value={userId} onChange={event => setUserId(event.target.value)}>
        <option value="">Select a user</option>
        {(users.data ?? []).map(user => <option key={user.id} value={user.id}>{user.displayName || user.username}</option>)}
      </select>
    </label>}
    {userId && privileges.isLoading && <p>Loading network permission…</p>}
    {userId && privileges.isError && <p role="alert">Could not load this user's network permission. Refresh and try again.</p>}
    {userId && privileges.data && <UserNetworkPrivilegesForm key={userId} userId={userId} canApproveOwn={privileges.data.canApproveOwn}
      isAdminViewer onSave={value => update.mutateAsync({ userId, privileges: value })} />}
  </div>;
}
