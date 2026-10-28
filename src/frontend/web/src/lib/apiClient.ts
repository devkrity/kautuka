/**
 * Centralized Axios client configuration and interceptors
 */
import axios from 'axios';
import type { AxiosError, AxiosRequestConfig } from 'axios';
import { getAccessToken, refreshAuthSession, reportAuthFailure } from '@/lib/authState';

const API_BASE = '/api';

const getApiUrl = (config?: AxiosRequestConfig): string => String(config?.url ?? '');
const isRefreshExemptAuthRoute = (url: string): boolean =>
  url.startsWith('/auth/login') ||
  url.startsWith('/auth/logout') ||
  url.startsWith('/auth/refresh') ||
  url.startsWith('/auth/setup');
const isFormDataPayload = (value: unknown): value is FormData =>
  typeof FormData !== 'undefined' && value instanceof FormData;
const ensureHeaders = (config: AxiosRequestConfig): Record<string, string> => {
  config.headers = config.headers ?? {};
  return config.headers as Record<string, string>;
};
const setAuthorizationHeader = (config: AxiosRequestConfig, token: string) => {
  const headers = ensureHeaders(config);
  headers.Authorization = `Bearer ${token}`;
};

export const apiClient = axios.create({
  baseURL: API_BASE,
  headers: {
    'Content-Type': 'application/json',
  },
  withCredentials: true,
});

const toError = (value: unknown): Error =>
  value instanceof Error ? value : new Error(String(value));

apiClient.interceptors.request.use((config) => {
  if (isFormDataPayload(config.data)) {
    const headers = ensureHeaders(config);
    delete headers['Content-Type'];
    delete headers['content-type'];
  }

  const token = getAccessToken();
  if (token) {
    setAuthorizationHeader(config, token);
  }
  return config;
});

apiClient.interceptors.response.use(
  (response) => response,
  async (error: unknown) => {
    const axiosError: AxiosError | null = axios.isAxiosError(error) ? error : null;
    const status = axiosError?.response?.status;
    const originalRequest = axiosError?.config as
      | (AxiosRequestConfig & { _retry?: boolean })
      | undefined;
    const requestUrl = getApiUrl(originalRequest);
    const refreshExemptAuthRoute = isRefreshExemptAuthRoute(requestUrl);

    if (status === 401 && originalRequest && !originalRequest._retry && !refreshExemptAuthRoute) {
      originalRequest._retry = true;
      try {
        const newToken = (await refreshAuthSession())?.accessToken ?? null;
        if (newToken) {
          setAuthorizationHeader(originalRequest, newToken);
          return apiClient(originalRequest);
        }
      } catch {
        // Fall through to auth failure handler
      }
    }

    if (status === 401 && !refreshExemptAuthRoute) {
      reportAuthFailure(toError(axiosError ?? error));
    }

    throw axiosError ?? error;
  }
);

export default apiClient;
