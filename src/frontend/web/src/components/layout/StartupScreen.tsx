import { BrandMark } from '@/components/BrandMark';
import { Button } from '@/components/ui/button';
import React from 'react';

type Phase = 'starting' | 'slow' | 'timeout';
type StartupScreenMode = 'starting' | 'reconnecting';

interface StartupScreenProps {
  mode?: StartupScreenMode;
}

export const StartupScreen: React.FC<StartupScreenProps> = ({ mode = 'starting' }) => {
  const [phase, setPhase] = React.useState<Phase>('starting');

  React.useEffect(() => {
    const slowTimer = setTimeout(() => setPhase('slow'), 12_000);
    const timeoutTimer = setTimeout(() => setPhase('timeout'), 55_000);
    return () => {
      clearTimeout(slowTimer);
      clearTimeout(timeoutTimer);
    };
  }, []);

  const headline =
    phase === 'starting'
      ? mode === 'reconnecting'
        ? 'Reconnecting to your AI services...'
        : 'Waking up your AI services...'
      : mode === 'reconnecting'
        ? 'Still reconnecting to services...'
        : 'Almost there, still starting up...';

  const detail =
    phase === 'starting'
      ? mode === 'reconnecting'
        ? 'The web UI is waiting for the backend to come back after a restart or update.'
        : 'This usually takes about 15-20 seconds on first launch.'
      : mode === 'reconnecting'
        ? 'Services are still restarting. The page will recover automatically as soon as they respond again.'
        : 'Taking a little longer than usual - this can happen after a fresh install or system restart. Hang tight!';

  const timeoutTitle =
    mode === 'reconnecting'
      ? 'Services are still reconnecting'
      : 'Services are taking longer than expected';

  const timeoutDetail =
    mode === 'reconnecting'
      ? 'The backend is still unavailable after the restart. You can wait a bit longer, or reload the page.'
      : 'Something might be preventing the AI services from starting. You can wait a bit longer, or restart the app.';

  return (
    <div className="flex min-h-screen w-full flex-col items-center justify-center bg-background p-6 text-foreground">
      <div className="max-w-sm text-center flex flex-col items-center gap-6">
        <BrandMark />

        <div className="space-y-2">
          {phase !== 'timeout' ? (
            <>
              <p className="text-sm font-medium text-foreground/80">{headline}</p>
              <p className="text-xs leading-relaxed text-muted-foreground">{detail}</p>
            </>
          ) : (
            <>
              <p className="text-sm font-medium text-attention">
                {timeoutTitle}
              </p>
              <p className="text-xs leading-relaxed text-muted-foreground">{timeoutDetail}</p>
              <Button
                onClick={() => globalThis.location.reload()}
                variant="secondary"
                className="mt-2 w-fit"
              >
                Reload
              </Button>
            </>
          )}
        </div>

        {phase !== 'timeout' && (
          <div className="flex gap-1.5">
            {[0, 1, 2].map((i) => (
              <div
                key={i}
                className="h-1.5 w-1.5 animate-bounce rounded-full bg-primary/50"
                style={{ animationDelay: `${i * 0.2}s` }}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
};
