import { Button } from '@/components/ui/button';
import { ExternalLink, ShieldCheck } from 'lucide-react';
import { useState } from 'react';
import type { ChannelSetupGuide } from './channelSetupGuides';

type ChannelSetupGuidePanelProps = {
  guide: ChannelSetupGuide;
  expanded: boolean;
  onCopyArtifact: (label: string, content: string) => Promise<boolean>;
  onOpenPortal?: () => Promise<string>;
};

export function ChannelSetupGuidePanel({
  guide,
  expanded,
  onCopyArtifact,
  onOpenPortal,
}: Readonly<ChannelSetupGuidePanelProps>) {
  const setupArtifact = guide.setupArtifact;
  const [copySucceeded, setCopySucceeded] = useState<boolean | null>(null);
  const [portalError, setPortalError] = useState<string | null>(null);

  const copyArtifact = async () => {
    if (!setupArtifact) return;
    setCopySucceeded(await onCopyArtifact(setupArtifact.label, setupArtifact.content));
  };

  const openPortal = () => {
    setPortalError(null);

    if (!onOpenPortal) {
      setPortalError('Open this setup link from your signed-in Kautuka settings.');
      return;
    }

    const popup = window.open('about:blank', '_blank');
    if (!popup) {
      setPortalError('Allow pop-ups to open the Slack app setup portal.');
      return;
    }
    popup.opener = null;

    void onOpenPortal()
      .then((url) => {
        if (new URL(url).origin !== 'https://api.slack.com') {
          throw new Error('Kautuka returned an invalid Slack app setup URL.');
        }
        popup.location.href = url;
      })
      .catch((error: unknown) => {
        popup.close();
        setPortalError(error instanceof Error ? error.message : 'Could not open Slack app setup.');
      });
  };

  return (
    <>
      {guide.portalRequiresAuth ? (
        <button
          type="button"
          className="inline-flex items-center gap-1 text-xs font-semibold text-primary hover:underline"
          onClick={openPortal}
        >
          {guide.portalLabel}
          <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
        </button>
      ) : (
        <a
          className="inline-flex items-center gap-1 text-xs font-semibold text-primary hover:underline"
          href={guide.portalUrl}
          target="_blank"
          rel="noreferrer"
        >
          {guide.portalLabel}
          <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
        </a>
      )}
      {portalError ? (
        <p className="mt-2 basis-full text-xs text-destructive" role="status">
          {portalError}
        </p>
      ) : null}

      <details className="mt-3 basis-full rounded-md border border-border/60 bg-muted/20 p-3" open={expanded}>
        <summary className="cursor-pointer text-xs font-semibold">
          Guided setup ({guide.steps.length} steps)
        </summary>
        <ol className="mt-3 space-y-3">
          {guide.steps.map((step, index) => (
            <li key={step.title} className="flex gap-2 text-xs">
              <span
                className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-primary-fill text-[10px] font-bold text-primary-foreground"
                aria-hidden="true"
              >
                {index + 1}
              </span>
              <span>
                <strong className="block text-foreground">
                  <span className="sr-only">Step {index + 1}: </span>
                  {step.title}
                </strong>
                <span className="text-muted-foreground">{step.detail}</span>
              </span>
            </li>
          ))}
        </ol>
        {setupArtifact ? (
          <Button
            className="mt-3"
            variant="secondary"
            onClick={() => void copyArtifact()}
          >
            {copySucceeded === true ? 'Copied' : setupArtifact.label}
          </Button>
        ) : null}
        {copySucceeded !== null ? (
          <p className="mt-2 text-[11px] text-muted-foreground" role="status">
            {copySucceeded
              ? 'Manifest copied. Paste it into the Slack app setup portal.'
              : 'Clipboard access was blocked. Follow the guided steps in the Slack portal.'}
          </p>
        ) : null}
        <div className="mt-3 rounded-md bg-accent/50 p-2.5">
          <p className="flex items-center gap-1.5 text-[11px] font-semibold text-foreground">
            <ShieldCheck className="h-3.5 w-3.5 text-primary" aria-hidden="true" />
            Kautuka handles automatically
          </p>
          <p className="mt-1 text-[11px] text-muted-foreground">
            {guide.automated.join(' · ')}
          </p>
        </div>
      </details>
    </>
  );
}
