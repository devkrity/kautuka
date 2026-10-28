import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';

import {
  settingsAdmin,
  type CatalogRecommendationItem,
  type CatalogRecommendationsResponse,
} from '../api/settingsAdmin';

export function useCatalogRecommendations() {
  return useQuery({
    queryKey: ['catalog-recommendations'],
    queryFn: async (): Promise<CatalogRecommendationsResponse> => {
      const response = await settingsAdmin.getCatalogRecommendations();
      return response.data;
    },
    staleTime: 300_000,
  });
}

export function useInstallCatalogRecommendation(targetKey: 'connectors' | 'skills') {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (recommendation: CatalogRecommendationItem) =>
      settingsAdmin.installCatalogRecommendation(recommendation.kind, recommendation.id),
    onSuccess: async (_, recommendation) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: [targetKey] }),
        queryClient.invalidateQueries({ queryKey: ['catalog-recommendations'] }),
      ]);
      toast.success(`Installed ${recommendation.title}.`);
    },
    onError: () => {
      toast.error('Hosted catalog is currently unreachable. Check again later.');
    },
  });
}