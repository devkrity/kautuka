import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import React from 'react';

type RecommendationsScreenProps = {
  onBack: () => void;
  onContinue: () => void;
  onSetupTelegram: () => void;
  canImportConnectors: boolean;
  canImportSkills: boolean;
};

export const RecommendationsScreen: React.FC<RecommendationsScreenProps> = ({
  onBack,
  onContinue,
  onSetupTelegram,
  canImportConnectors,
  canImportSkills,
}) => {
  const destinations = [
    canImportConnectors ? 'Connectors' : null,
    canImportSkills ? 'Skills' : null,
  ].filter((value): value is string => value !== null);
  const destinationSummary = destinations.length > 1 ? destinations.join(' and ') : destinations[0] ?? 'Settings';

  return (
    <div className="animate-in fade-in slide-in-from-right-8 duration-300 space-y-8">
      <div>
        <h2 className="text-2xl font-bold">Step 6: Add abilities</h2>
        <p className="text-muted-foreground mt-1 max-w-3xl">
          Your helper is ready. You can add more local abilities now or come back later.
        </p>
      </div>

      <div className="rounded-xl border border-border bg-card p-6 space-y-3 shadow-sm">
        <h3 className="text-base font-semibold">What you can add next</h3>
        <p className="text-sm text-muted-foreground leading-relaxed">
          Kautuka starts minimal on purpose. Browse {destinationSummary} when you want to add more
          things your helper can do from Kautuka.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
          <div className="text-base font-semibold">Connect Telegram</div>
          <p className="mt-2 text-sm text-muted-foreground leading-relaxed">
            Add a lightweight remote inbox so you can chat with your helper and handle approvals away from your desk.
          </p>
          <Button className="mt-4" onClick={onSetupTelegram}>Set up Telegram</Button>
        </div>
        <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
          <div className="text-base font-semibold">Install recommended tools</div>
          <p className="mt-2 text-sm text-muted-foreground leading-relaxed">
            Use Connectors and Skills later for web access, documents, and other local capabilities.
          </p>
        </div>
      </div>

      <Alert variant="info" className="shadow-sm">
        <div className="font-medium">Where to manage them later</div>
        <p className="mt-1 leading-relaxed">
          After onboarding, open Connectors for runtime tools and Skills for instruction packs. The
          recommended catalog lives there whenever you want to add more capabilities.
        </p>
      </Alert>

      <div className="flex justify-between gap-3">
        <Button variant="secondary" onClick={onBack}>
          Back
        </Button>
        <Button onClick={onContinue}>Skip for now</Button>
      </div>
    </div>
  );
};