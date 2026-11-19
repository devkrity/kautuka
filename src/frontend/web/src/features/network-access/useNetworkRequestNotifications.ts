import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import type { NetworkAccessRequestDto } from './networkAccessTypes';

export function useNetworkRequestNotifications(
  authenticated: boolean,
  requests: NetworkAccessRequestDto[] | undefined
) {
  const seen = useRef(new Set<string>());
  const navigate = useNavigate();

  useEffect(() => {
    if (!authenticated) {
      seen.current.clear();
      return;
    }
    if (!requests) return;
    const fresh = requests.filter((request) => request.status === 'pending' && !seen.current.has(request.id));
    for (const request of fresh) seen.current.add(request.id);
    if (fresh.length === 0) return;
    const message = fresh.length === 1
      ? `Network access blocked: ${fresh[0].destination}`
      : `${fresh.length} network access requests were blocked`;
    toast.warning(message, {
      description: 'Review the request in Network Access.',
      action: { label: 'Review', onClick: () => navigate('/network-access') },
      duration: 10000,
    });
  }, [authenticated, requests, navigate]);
}
