import React from 'react';
import { Button } from '@/components/ui/button';
import { listExternalProviders, startExternalLogin } from '../api/auth';

interface ExternalSignInMethodsProps {
  linkedProviders: string[];
  onError: (message: string) => void;
  onRedirect?: (url: string) => void;
}

export function ExternalSignInMethods({
  linkedProviders,
  onError,
  onRedirect = (url) => globalThis.location.assign(url),
}: Readonly<ExternalSignInMethodsProps>) {
  const [available, setAvailable] = React.useState({ google: false, microsoft: false });
  const [linking, setLinking] = React.useState<string | null>(null);

  React.useEffect(() => {
    let active = true;
    void listExternalProviders().then((providers) => {
      if (active) setAvailable(providers);
    });
    return () => {
      active = false;
    };
  }, []);

  const linked = new Set(linkedProviders.map((provider) => provider.toLowerCase()));
  const beginLink = async (provider: 'google' | 'microsoft', accountType?: 'organizations' | 'consumers') => {
    const key = accountType ? `${provider}-${accountType}` : provider;
    setLinking(key);
    try {
      const started = await startExternalLogin(provider, accountType, 'link');
      onRedirect(started.startUrl);
    } catch (error) {
      onError(error instanceof Error ? error.message : 'Failed to start account linking.');
      setLinking(null);
    }
  };

  return (
    <section className="space-y-2" aria-labelledby="linked-sign-in-title">
      <div>
        <h3 id="linked-sign-in-title" className="text-sm font-semibold">Sign-in methods</h3>
        <p className="text-xs text-muted-foreground">Link another provider to sign in to this account.</p>
      </div>
      <div className="flex flex-wrap justify-end gap-2">
        {available.google && !linked.has('google') ? (
          <Button type="button" variant="secondary" disabled={linking !== null} onClick={() => void beginLink('google')}>
            {linking === 'google' ? 'Opening Google…' : 'Link Google account'}
          </Button>
        ) : null}
        {available.microsoft && !linked.has('microsoft') ? (
          <>
            <Button type="button" variant="secondary" disabled={linking !== null} onClick={() => void beginLink('microsoft', 'organizations')}>
              {linking?.startsWith('microsoft-') ? 'Opening Microsoft…' : 'Link work or school account'}
            </Button>
            <Button type="button" variant="secondary" disabled={linking !== null} onClick={() => void beginLink('microsoft', 'consumers')}>
              {linking?.startsWith('microsoft-') ? 'Opening Microsoft…' : 'Link personal Microsoft account'}
            </Button>
          </>
        ) : null}
        {!available.google && !available.microsoft ? (
          <p className="text-xs text-muted-foreground">External sign-in providers are not configured.</p>
        ) : null}
      </div>
    </section>
  );
}
