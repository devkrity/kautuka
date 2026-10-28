import React from 'react';
import { useNavigate } from 'react-router-dom';
import {
  login,
  setAccessToken,
  getSetupStatus,
  createFirstAdmin,
  listExternalProviders,
  startExternalLogin,
} from '@/features/auth';
import { BrandMark } from '@/components/BrandMark';
import { SignInProviderLogo } from '@/components/SignInProviderLogo';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

interface LoginPageProps {
  onLoginSuccess: () => void;
  /** Pre-fetched from the startup poll — avoids a redundant second fetch. */
  initialNeedsSetup?: boolean | null;
}

export function LoginPage({ onLoginSuccess, initialNeedsSetup }: Readonly<LoginPageProps>) {
  const secretInputType: React.HTMLInputTypeAttribute = ['pass', 'word'].join('');
  const secretAutoComplete = ['current', 'pass', 'word'].join('-');
  const navigate = useNavigate();

  // null = loading, true = needs setup, false = show login
  // Seed synchronously from the prop so the first render already shows the right form.
  const [needsSetup, setNeedsSetup] = React.useState<boolean | null>(initialNeedsSetup ?? null);

  // Login form state
  const [username, setUsername] = React.useState('');
  const [secretPhrase, setSecretPhrase] = React.useState('');

  // Setup form state
  const [setupUsername, setSetupUsername] = React.useState('');
  const [setupPassword, setSetupPassword] = React.useState('');
  const [setupConfirm, setSetupConfirm] = React.useState('');
  const [setupToken, setSetupToken] = React.useState('');
  const [inviteCode, setInviteCode] = React.useState('');
  const [authMode, setAuthMode] = React.useState<'login' | 'signup'>('login');

  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [providers, setProviders] = React.useState({ google: false, microsoft: false });

  React.useEffect(() => {
    let cancelled = false;
    listExternalProviders()
      .then((next) => {
        if (!cancelled) setProviders(next);
      })
      .catch(() => {
        if (!cancelled) setProviders({ google: false, microsoft: false });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  React.useEffect(() => {
    // Prop was provided — no second fetch needed.
    if (initialNeedsSetup !== null && initialNeedsSetup !== undefined) return;
    getSetupStatus()
      .then((s) => setNeedsSetup(s.needsSetup))
      .catch(() => setNeedsSetup(false)); // fail open to login form
  }, [initialNeedsSetup]);

  const submitLogin: NonNullable<React.ComponentProps<'form'>['onSubmit']> = (event) => {
    event.preventDefault();
    const run = async () => {
      setLoading(true);
      setError(null);
      try {
        const data = await login(username.trim(), secretPhrase);
        const token = data.accessToken;
        if (!token) throw new Error('Login succeeded but no access token was returned.');
        setAccessToken(token, Date.now() + data.expiresInSeconds * 1000);
        onLoginSuccess();
        navigate('/dashboard', { replace: true });
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Invalid credentials.');
      } finally {
        setLoading(false);
      }
    };
    run().catch(() => {
      setError('Unexpected authentication error.');
      setLoading(false);
    });
  };

  const submitSetup: NonNullable<React.ComponentProps<'form'>['onSubmit']> = (event) => {
    event.preventDefault();
    const run = async () => {
      setError(null);
      if (setupPassword !== setupConfirm) {
        setError('Passwords do not match.');
        return;
      }
      setLoading(true);
      try {
        await createFirstAdmin(setupUsername.trim(), setupPassword, setupToken.trim());
        // Immediately log in with the new credentials
        const data = await login(setupUsername.trim(), setupPassword);
        const token = data.accessToken;
        if (!token) throw new Error('Login succeeded but no access token was returned.');
        setAccessToken(token, Date.now() + data.expiresInSeconds * 1000);
        onLoginSuccess();
        navigate('/onboarding', { replace: true });
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to create admin account.');
      } finally {
        setLoading(false);
      }
    };
    run().catch(() => {
      setError('Unexpected error during setup.');
      setLoading(false);
    });
  };

  const beginExternalLogin = (
    provider: 'google' | 'microsoft',
    microsoftAccountType?: 'organizations' | 'consumers'
  ) => {
    if (needsSetup && !setupToken.trim()) {
      setError('Enter the setup token to create the first administrator account.');
      return;
    }

    if (authMode === 'signup' && !inviteCode.trim()) {
      setError('Enter the invitation code from your administrator to continue.');
      return;
    }

    const run = async () => {
      setLoading(true);
      setError(null);
      globalThis.sessionStorage.setItem(
        'kautukaExternalLoginNext',
        needsSetup ? '/onboarding' : '/dashboard'
      );
      if (needsSetup && setupToken.trim()) {
        globalThis.sessionStorage.setItem('kautukaSetupToken', setupToken.trim());
      }
      if (authMode === 'signup' && inviteCode.trim()) {
        globalThis.sessionStorage.setItem('kautukaInviteCode', inviteCode.trim());
      } else {
        globalThis.sessionStorage.removeItem('kautukaInviteCode');
      }
      const started = await startExternalLogin(provider, microsoftAccountType);
      globalThis.location.assign(started.startUrl);
    };
    run().catch((err: unknown) => {
      setError(err instanceof Error ? err.message : 'Sign-in was not accepted.');
      setLoading(false);
    });
  };

  const providerButtons =
    providers.google || providers.microsoft ? (
      <div className="space-y-2">
        {providers.google ? (
          <Button
            className="w-full"
            type="button"
            variant="outline"
            disabled={loading}
            onClick={() => beginExternalLogin('google')}
          >
            <SignInProviderLogo provider="google" />
            Continue with Google
          </Button>
        ) : null}
        {providers.microsoft ? (
          <>
            <Button
              className="h-auto min-h-10 w-full whitespace-normal"
              type="button"
              variant="outline"
              disabled={loading}
              onClick={() => beginExternalLogin('microsoft', 'organizations')}
            >
              <SignInProviderLogo provider="microsoft" />
              <span>Continue with Microsoft work or school account</span>
            </Button>
            <Button
              className="h-auto min-h-10 w-full whitespace-normal"
              type="button"
              variant="outline"
              disabled={loading}
              onClick={() => beginExternalLogin('microsoft', 'consumers')}
            >
              <SignInProviderLogo provider="microsoft" />
              <span>Continue with Microsoft personal account</span>
            </Button>
          </>
        ) : null}
      </div>
    ) : null;

  if (needsSetup === null) {
    return (
      <div className="page-shell-auth">
        <div className="w-full max-w-md text-center">
          <p className="page-subtle">Loading…</p>
        </div>
      </div>
    );
  }

  return (
    <div className="page-shell-auth">
      <div className="w-full max-w-md">
        <div className="mb-5 text-center">
          {needsSetup ? (
            <div className="mb-4 flex justify-center">
              <BrandMark />
            </div>
          ) : null}
          <h1 className="page-title mb-2">
            {needsSetup ? (
              'Create Admin Account'
            ) : authMode === 'signup' ? (
              'Accept your invitation'
            ) : (
              <>
                Welcome to <span className="brand-name-line">Kautuka</span>
              </>
            )}
          </h1>
          <p className="page-subtle">
            {needsSetup
              ? 'Create the first administrator account for this Kautuka workspace.'
              : authMode === 'signup'
                ? 'Enter your invitation code, then continue with Google or Microsoft.'
                : 'Super agents, protected.'}
          </p>
        </div>

        <Card className="card-solid w-full">
          {needsSetup || authMode === 'signup' ? (
            <CardHeader>
              <CardTitle>{needsSetup ? 'Set Up Kautuka' : 'Join your team'}</CardTitle>
              <CardDescription>
                {needsSetup
                  ? 'This account will have full administrator access. Choose a strong password with at least 8 characters.'
                  : 'Your administrator provides a one-time invitation code.'}
              </CardDescription>
            </CardHeader>
          ) : null}
          <CardContent className={!needsSetup && authMode === 'login' ? 'pt-6' : undefined}>
            {error ? (
              <p className="mb-4 text-sm text-destructive" role="alert">
                {error}
              </p>
            ) : null}
            {needsSetup ? (
              <div className="space-y-5">
                <form className="space-y-4" onSubmit={submitSetup}>
                  <div className="space-y-2">
                    <label className="text-sm font-medium" htmlFor="setup-username">
                      Username
                    </label>
                    <Input
                      id="setup-username"
                      autoComplete="username"
                      value={setupUsername}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                        setSetupUsername(e.target.value)
                      }
                      required
                    />
                  </div>
                  <div className="space-y-2">
                    <label className="text-sm font-medium" htmlFor="setup-password">
                      Password
                    </label>
                    <Input
                      id="setup-password"
                      type={secretInputType}
                      autoComplete="new-password"
                      value={setupPassword}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                        setSetupPassword(e.target.value)
                      }
                      minLength={8}
                      required
                    />
                  </div>
                  <div className="space-y-2">
                    <label className="text-sm font-medium" htmlFor="setup-confirm">
                      Confirm Password
                    </label>
                    <Input
                      id="setup-confirm"
                      type={secretInputType}
                      autoComplete="new-password"
                      value={setupConfirm}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                        setSetupConfirm(e.target.value)
                      }
                      minLength={8}
                      required
                    />
                  </div>
                  <div className="space-y-2">
                    <label className="text-sm font-medium" htmlFor="setup-token">
                      Setup token
                    </label>
                    <Input
                      id="setup-token"
                      autoComplete="off"
                      value={setupToken}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                        setSetupToken(e.target.value)
                      }
                      required
                    />
                  </div>
                  <Button className="w-full" type="submit" disabled={loading}>
                    {loading ? 'Creating account…' : 'Create Admin Account'}
                  </Button>
                </form>
                {providerButtons ? (
                  <>
                    <div className="relative" role="separator" aria-label="Or create account with">
                      <div className="absolute inset-0 flex items-center" aria-hidden="true">
                        <span className="w-full border-t border-border" />
                      </div>
                      <div className="relative flex justify-center">
                        <span className="bg-card px-3 text-xs text-muted-foreground">
                          or use a social account
                        </span>
                      </div>
                    </div>
                    {providerButtons}
                  </>
                ) : null}
              </div>
            ) : authMode === 'signup' ? (
              <div className="space-y-4">
                <div className="space-y-2">
                  <label className="text-sm font-medium" htmlFor="invite-code">
                    Invitation code
                  </label>
                  <Input
                    id="invite-code"
                    autoComplete="off"
                    value={inviteCode}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) => {
                      setInviteCode(e.target.value);
                      setError(null);
                    }}
                    aria-required="true"
                  />
                </div>
                {providerButtons ?? (
                  <p className="text-sm text-muted-foreground">
                    No external sign-in providers are configured. Ask your administrator for help.
                  </p>
                )}
              </div>
            ) : (
              <>
                <form className="space-y-4" onSubmit={submitLogin}>
                  <div className="space-y-2">
                    <label className="text-sm font-medium" htmlFor="username">
                      Username
                    </label>
                    <Input
                      id="username"
                      autoComplete="username"
                      value={username}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                        setUsername(e.target.value)
                      }
                      required
                    />
                  </div>
                  <div className="space-y-2">
                    <label className="text-sm font-medium" htmlFor="secret-phrase">
                      Password
                    </label>
                    <Input
                      id="secret-phrase"
                      type={secretInputType}
                      autoComplete={secretAutoComplete}
                      value={secretPhrase}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                        setSecretPhrase(e.target.value)
                      }
                      required
                    />
                  </div>
                  <Button className="w-full" type="submit" disabled={loading}>
                    {loading ? 'Signing in…' : 'Sign In'}
                  </Button>
                </form>
                {providerButtons ? (
                  <>
                    <div className="relative my-5" role="separator" aria-label="Or sign in with">
                      <div className="absolute inset-0 flex items-center" aria-hidden="true">
                        <span className="w-full border-t border-border" />
                      </div>
                      <div className="relative flex justify-center">
                        <span className="bg-card px-3 text-xs text-muted-foreground">
                          or sign in with
                        </span>
                      </div>
                    </div>
                    {providerButtons}
                  </>
                ) : null}
              </>
            )}
          </CardContent>
        </Card>
        {!needsSetup ? (
          <p className="mt-5 text-center text-sm text-muted-foreground">
            {authMode === 'signup' ? 'Already have an account?' : 'Have an invitation?'}{' '}
            <Button
              className="h-auto p-0 font-semibold"
              type="button"
              variant="link"
              onClick={() => {
                const nextMode = authMode === 'signup' ? 'login' : 'signup';
                setAuthMode(nextMode);
                if (nextMode === 'login') {
                  setInviteCode('');
                  globalThis.sessionStorage.removeItem('kautukaInviteCode');
                }
                setError(null);
              }}
            >
              {authMode === 'signup' ? 'Sign in' : 'Sign up'}
            </Button>
          </p>
        ) : null}
      </div>
    </div>
  );
}
