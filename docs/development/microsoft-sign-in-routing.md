# Microsoft sign-in account routing

The login and first-run screens offer separate Microsoft work/school and personal
account buttons, alongside Google sign-in. Provider buttons include local SVG logos.

`POST /api/auth/external/start` accepts an optional `microsoftAccountType` of
`organizations` or `consumers` for the Microsoft provider. The gateway passes this
selection in the hosted start fragment. The auth broker validates the selection,
forwards it to its authorize handler, and uses the corresponding Microsoft v2
authorization endpoint. Missing selections retain the existing `/common` route.
Invalid selections and selections on other providers are rejected.

This is a temporary routing workaround for the observed Microsoft `/common`
personal-account discovery failure. The app registration permits both organizational
and personal accounts, but the captured Microsoft page initialized its discovery
logic as organizational-only. The underlying cause of that Microsoft behaviour
remains unresolved.

The same Microsoft application, identity provider key, callback, PKCE, nonce and
identity-token validation are used for both buttons. Mailbox connector OAuth remains
separate. Deploy the corresponding `kautuka-auth` broker change before releasing
the updated appliance UI.

## Validation on 2026-10-13

- Rebuilt and restarted the local native stack successfully; solution build reported
  zero warnings and zero errors.
- TypeScript check passed; 10 login-page tests, 7 backend external-login tests and
  22 auth-broker regression tests passed.
- Focused broker checks confirmed both account routes and rejection of invalid or
  cross-provider account selections.
- Live work/school button reached `/organizations`; full work/school authentication
  was not exercised.
- Live personal button reached Microsoft's consumer sign-in flow. Manually entered
  personal email reached password/passkey/mobile authentication options. Existing
  personal-account selection completed token exchange and returned to the dashboard.
- Fresh credential authentication after manual email entry was not completed.
