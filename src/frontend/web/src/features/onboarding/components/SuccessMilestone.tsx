import React, { useState } from 'react';
import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';

interface SuccessMilestoneProps {
  onContinue: () => void;
  onReviewAbilities: () => void;
  onOpenSettings: () => void;
  telegramConfigured?: boolean;
}

export function SuccessMilestone({
  onContinue,
  onReviewAbilities,
  onOpenSettings,
  telegramConfigured = false,
}: Readonly<SuccessMilestoneProps>) {
  return (
    <div className="flex flex-col items-center justify-center space-y-8 animate-in fade-in slide-in-from-bottom-4 duration-500">
      <div className="w-24 h-24 rounded-full bg-done/20 flex items-center justify-center">
        <span className="text-5xl">🎉</span>
      </div>
      <div className="max-w-2xl text-center space-y-4">
        <h1 className="text-4xl font-extrabold tracking-tight text-done">
          Your helper is ready
        </h1>
        <p className="text-xl text-muted-foreground">
          Kautuka is set up on this installation, your AI connection is ready, and your helper can
          start working now.
        </p>
        <p className="text-sm text-muted-foreground">
          Your AI model can run locally or in the cloud, but Kautuka runs here and performs tool work here.
        </p>
        {telegramConfigured && (
          <Alert variant="info" className="text-left">
            <div className="font-medium">Telegram is connected</div>
            <p className="mt-1 text-xs">
              Later, if you want to verify it, go to Settings → Users → Telegram Settings and use
              Send Telegram Test.
            </p>
          </Alert>
        )}
      </div>
      <div className="flex flex-col sm:flex-row gap-4 pt-4 w-full max-w-md">
        <Button
          size="lg"
          onClick={onContinue}
          className="flex-1 font-semibold whitespace-normal md:whitespace-nowrap"
        >
          Start chatting
        </Button>
        <Button
          size="lg"
          variant="secondary"
          onClick={onReviewAbilities}
          className="flex-1 whitespace-normal md:whitespace-nowrap"
        >
          Review abilities
        </Button>
        <Button
          size="lg"
          variant="secondary"
          onClick={onOpenSettings}
          className="flex-1 whitespace-normal md:whitespace-nowrap"
        >
          Open settings
        </Button>
      </div>
    </div>
  );
}
