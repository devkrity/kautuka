# Users, authentication, and organization

The first-run form creates the first administrator. Later account and organization management is administrator-only under `/settings`.

Google and Microsoft sign-in is available only when this appliance is configured with the hosted auth broker and that provider’s public client id. A source checkout keeps the local username and password form. The first person who can open first-run can become the administrator, including with Google or Microsoft; set a local password after that sign-in. Every later social sign-in must match one active invited user by verified email. That match is not Entra tenant membership and not a domain allowlist. The durable link is the provider subject. Changing the user’s email does not move the link. Removing the last remaining sign-in method is rejected. There is no email password reset.

## Accounts and access

- Invite someone from **Settings → Users** with the email their Google or Microsoft account will verify. A username and secret phrase are only for local sign-in. An invited account with no secret phrase has no local password until that person sets one.
- An administrator can remove another user’s Google or Microsoft link. That removal signs the other user out. Removing your own link does not.
- User access can be limited to selected agents.
- Deactivation blocks authenticated use without deleting the user’s records.
- Users can change their own profile, password, and channel settings from the profile dialog.
- Administrators can configure channels and run channel HITL tests for users.

Admin routes are enforced by both the UI guard and Gateway authorization. Do not treat a hidden tab as the security boundary.

## Organization

**Settings → Organization** stores organization name and industry. These values become default persona context; individual agent personas can override them. Leaving them blank is supported.

## Endpoints and verification

- Setup/session: `/api/auth/setup/status`, `/api/auth/setup`, `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout`, `/api/auth/me`
- Social sign-in: `/api/auth/external/providers`, `/api/auth/external/start`, `/api/auth/external/complete`, browser return `/login/callback`
- Users: `/api/settings/users`, `/api/settings/users/{userId}/...`, `/api/settings/users/{userId}/external-logins/{provider}`
- Self-service: `/api/settings/me/profile`, `/api/settings/me/password`, `/api/settings/me/channels`, `/api/settings/me/external-logins/{provider}`
- Organization: `GET/PUT /api/settings/organization`

Verify with a separate non-admin account: confirm it sees only permitted agents, cannot open admin Settings, and can update only its own profile and channels.
