/**
 * TanStack Query provider wrapper
 */
import React from 'react';
import { QueryClientProvider } from '@tanstack/react-query';
import { DirectionProvider } from '@radix-ui/react-direction';
import { queryClient } from '@/lib/queryClient';

type QueryProviderProps = Readonly<{
  children: React.ReactNode;
}>;

export function QueryProvider({ children }: QueryProviderProps) {
  return (
    <DirectionProvider dir="ltr">
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    </DirectionProvider>
  );
}
