import { getAccessToken, refreshAuthSession, reportAuthFailure } from '@/lib/authState';

function withCurrentAuthorization(headersInit?: HeadersInit): Headers {
  const headers = new Headers(headersInit);
  const accessToken = getAccessToken();
  if (accessToken) {
    headers.set('Authorization', `Bearer ${accessToken}`);
  } else {
    headers.delete('Authorization');
  }
  return headers;
}

/** Fetches an authenticated resource, coalescing refresh and retrying one 401 exactly once. */
export async function authenticatedFetch(
  input: RequestInfo | URL,
  init: RequestInit = {}
): Promise<Response> {
  const requestInit: RequestInit = {
    ...init,
    credentials: init.credentials ?? 'include',
    headers: withCurrentAuthorization(init.headers),
  };
  const response = await fetch(input, requestInit);
  if (response.status !== 401) {
    return response;
  }

  const refreshed = await refreshAuthSession();
  if (!refreshed) {
    reportAuthFailure(new Error('Authentication session expired.'));
    return response;
  }

  const retriedResponse = await fetch(input, {
    ...requestInit,
    headers: withCurrentAuthorization(init.headers),
  });
  if (retriedResponse.status === 401) {
    reportAuthFailure(new Error('Authentication session expired.'));
  }
  return retriedResponse;
}
