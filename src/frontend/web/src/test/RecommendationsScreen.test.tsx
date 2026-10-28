import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { RecommendationsScreen } from '@/features/onboarding/components/RecommendationsScreen';

describe('RecommendationsScreen', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows informational guidance about managing catalog items later', () => {
    render(
      <RecommendationsScreen
        canImportConnectors={true}
        canImportSkills={true}
        onBack={vi.fn()}
        onContinue={vi.fn()}
        onSetupTelegram={vi.fn()}
      />
    );

    expect(screen.getByText('Step 6: Add abilities')).toBeInTheDocument();
    expect(
      screen.getByText(
        'After onboarding, open Connectors for runtime tools and Skills for instruction packs. The recommended catalog lives there whenever you want to add more capabilities.'
      )
    ).toBeInTheDocument();
  });

  it('mentions the specific destination pages available to the user', () => {
    render(
      <RecommendationsScreen
        canImportConnectors={true}
        canImportSkills={false}
        onBack={vi.fn()}
        onContinue={vi.fn()}
        onSetupTelegram={vi.fn()}
      />
    );

    expect(screen.getByText(/browse Connectors when you want to add more/i)).toBeInTheDocument();
  });

  it('offers Telegram as an optional ability from the abilities step', () => {
    render(
      <RecommendationsScreen
        canImportConnectors={false}
        canImportSkills={false}
        onBack={vi.fn()}
        onContinue={vi.fn()}
        onSetupTelegram={vi.fn()}
      />
    );

    expect(screen.getByText('Connect Telegram')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Set up Telegram' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Skip for now' })).toBeInTheDocument();
  });
});