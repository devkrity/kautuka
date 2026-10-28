import { createCommandClient } from '@devkrity/narad';
import { authenticatedFetch } from '@/lib/authenticatedFetch';

export const naradCommandClient = createCommandClient({
  baseUrl: '/api/narad/v1',
  fetch: (input, init) => authenticatedFetch(input, init ?? {}),
});
