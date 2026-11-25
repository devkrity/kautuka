import type { AxiosError } from 'axios';

type ProviderPermission = {
  destination: string;
  port: number;
  allowLan: boolean;
  revision: string;
};

export function isProviderIdReserved(error: unknown): boolean {
  const response = (error as AxiosError<{ code?: string }> | null)?.response;
  return response?.status === 409 && response.data?.code === 'provider_id_reserved';
}

export async function confirmProviderSave(
  save: (body: Record<string, unknown>) => Promise<unknown>,
  body: Record<string, unknown>,
): Promise<boolean> {
  try {
    await save(body);
    return true;
  } catch (error) {
    const response = (error as AxiosError<Partial<ProviderPermission>>).response;
    const permission = response?.data;
    if (response?.status !== 409 || typeof permission?.destination !== 'string'
      || typeof permission.port !== 'number' || typeof permission.allowLan !== 'boolean'
      || typeof permission.revision !== 'string') {
      throw error;
    }

    const localNetwork = permission.allowLan ? 'This includes access to the local network.' : 'Local network access is excluded.';
    if (!window.confirm(`Allow everyone on this appliance to reach ${permission.destination}:${permission.port} and save this provider?\n\n${localNetwork}`)) {
      return false;
    }

    await save({
      ...body,
      networkPermission: {
        destination: permission.destination,
        port: permission.port,
        allowLan: permission.allowLan,
        revision: permission.revision,
      },
    });
    return true;
  }
}
