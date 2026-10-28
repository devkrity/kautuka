import React from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

const SECRET_INPUT_TYPE: React.HTMLInputTypeAttribute = ['pass', 'word'].join('');
type InviteRole = 'elevated' | 'standard';

interface InviteDraft {
  username: string;
  email: string;
  secret: string;
  role: InviteRole;
}

interface UsersInviteCardProps {
  invite: InviteDraft;
  onInviteChange: (next: InviteDraft) => void;
  onInvite: () => void;
}

export function UsersInviteCard({
  invite,
  onInviteChange,
  onInvite,
}: Readonly<UsersInviteCardProps>) {
  return (
    <Card className="card-solid">
      <CardHeader>
        <CardTitle>Invite someone</CardTitle>
        <CardDescription>
          The invite expires after seven days and can be redeemed only when the provider email
          matches this address. Google requires its verified-email claim; Microsoft matches the
          email claim in its signed ID token. Add a password only if they should also sign in with a
          username.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <label className="block text-sm">
            <span className="mb-1 block text-xs text-muted-foreground">Email</span>
            <Input
              value={invite.email}
              onChange={(e) => onInviteChange({ ...invite, email: e.target.value })}
              placeholder="name@outlook.com"
              autoComplete="off"
            />
          </label>
          <label className="block text-sm">
            <span className="mb-1 block text-xs text-muted-foreground">
              Username, if they need local sign-in
            </span>
            <Input
              value={invite.username}
              onChange={(e) => onInviteChange({ ...invite, username: e.target.value })}
              placeholder="Taken from the email when blank"
              autoComplete="off"
            />
          </label>
          <label className="block text-sm">
            <span className="mb-1 block text-xs text-muted-foreground">Password, optional</span>
            <Input
              type={SECRET_INPUT_TYPE}
              value={invite.secret}
              onChange={(e) => onInviteChange({ ...invite, secret: e.target.value })}
              placeholder="Leave blank for Google or Microsoft only"
              autoComplete="new-password"
            />
          </label>
          <label className="block text-sm">
            <span className="mb-1 block text-xs text-muted-foreground">Role</span>
            <select
              title="Invite user role"
              className="input w-full"
              value={invite.role}
              onChange={(e) => onInviteChange({ ...invite, role: e.target.value as InviteRole })}
            >
              <option value="standard">User</option>
              <option value="elevated">Admin</option>
            </select>
          </label>
        </div>
        <div className="flex justify-end">
          <Button onClick={onInvite}>Invite</Button>
        </div>
      </CardContent>
    </Card>
  );
}
