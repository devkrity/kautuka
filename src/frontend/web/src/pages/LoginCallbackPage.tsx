import React from 'react';
import { useNavigate } from 'react-router-dom';
import {
  completeExternalLink,
  completeExternalLogin,
  externalLoginIntent,
  setAccessToken,
} from '@/features/auth';
import { BrandMark } from '@/components/BrandMark';

interface LoginCallbackPageProps {
  onLoginSuccess: () => void;
}

function readCallbackParams(): {
  code: string;
  state: string;
  brokerTicket: string;
  error: string;
} {
  const stored = globalThis.sessionStorage.getItem('kautukaLoginCallbackHash') ?? '';
  globalThis.sessionStorage.removeItem('kautukaLoginCallbackHash');
  const params = new URLSearchParams(stored.startsWith('#') ? stored.slice(1) : stored);
  const unique = (name: string) => {
    const values = params.getAll(name);
    return values.length === 1 ? values[0] : '';
  };
  return {
    code: unique('code'),
    state: unique('state'),
    brokerTicket: unique('broker_ticket'),
    error: params.getAll('error').length <= 1 ? (params.get('error') ?? '') : 'invalid',
  };
}

export function LoginCallbackPage({ onLoginSuccess }: Readonly<LoginCallbackPageProps>) {
  const navigate = useNavigate();
  const [message, setMessage] = React.useState('Completing sign-in…');

  React.useEffect(() => {
    const params = readCallbackParams();
    if (
      params.error ||
      params.code.length === 0 ||
      params.state.length === 0 ||
      params.brokerTicket.length === 0
    ) {
      setMessage('Sign-in was not accepted.');
      return;
    }

    const run = async () => {
      const intent = externalLoginIntent(params.state);
      const data =
        intent === 'link'
          ? await completeExternalLink(params.code, params.state, params.brokerTicket)
          : await completeExternalLogin(params.code, params.state, params.brokerTicket);
      if (!data.accessToken) {
        throw new Error('Sign-in was not accepted.');
      }

      setAccessToken(data.accessToken, Date.now() + data.expiresInSeconds * 1000);
      const next = globalThis.sessionStorage.getItem('kautukaExternalLoginNext') ?? '/dashboard';
      globalThis.sessionStorage.removeItem('kautukaExternalLoginNext');
      onLoginSuccess();
      navigate(next === '/onboarding' ? '/onboarding' : '/dashboard', { replace: true });
    };

    run().catch(() => {
      setMessage('Sign-in was not accepted.');
    });
  }, [navigate, onLoginSuccess]);

  return (
    <div className="page-shell-auth">
      <div className="w-full max-w-md text-center">
        <div className="mb-4 flex justify-center">
          <BrandMark />
        </div>
        <p className="page-subtle">{message}</p>
      </div>
    </div>
  );
}
