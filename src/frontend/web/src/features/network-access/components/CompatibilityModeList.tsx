import React from 'react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { RELAXATION_LABELS } from './CompatibilityModeCard';
import type { ImageCompatProfileDto } from '../networkAccess';

interface CompatibilityModeListProps {
  profiles: ImageCompatProfileDto[];
  onRemove: (imageDigest: string) => Promise<unknown>;
  disabled?: boolean;
}

export function CompatibilityModeList({
  profiles,
  onRemove,
  disabled = false,
}: CompatibilityModeListProps) {
  const [removingDigest, setRemovingDigest] = React.useState<string | null>(null);

  const handleRemove = async (digest: string) => {
    setRemovingDigest(digest);
    try {
      await onRemove(digest);
    } finally {
      setRemovingDigest(null);
    }
  };

  return (
    <section className="space-y-3" aria-labelledby="compat-images-heading">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id="compat-images-heading" className="text-lg font-semibold">
          Images in compatibility mode
        </h3>
        <Badge variant="outline">{profiles.length}</Badge>
      </div>
      <p className="text-sm text-muted-foreground">
        Third-party container images granted relaxed execution permissions. Network access
        controls still apply.
      </p>

      {profiles.length === 0 ? (
        <p className="text-sm text-muted-foreground py-4">No images in compatibility mode.</p>
      ) : (
        <div className="space-y-3">
          {profiles.map((profile) => {
            const formattedDate = new Date(profile.grantedAtUtc).toLocaleString();
            return (
              <article
                key={profile.imageDigest}
                data-testid={`compat-profile-${profile.imageDigest}`}
                className="flex flex-col md:flex-row md:items-center justify-between gap-3 rounded-xl border border-border bg-card p-4 shadow-sm"
              >
                <div className="space-y-2 min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-semibold text-sm">
                      {profile.connectorDisplayName || 'Connector image'}
                    </span>
                    <Badge variant="attention">Compatibility mode (relaxed settings)</Badge>
                  </div>

                  <p className="text-xs font-mono text-muted-foreground break-all">
                    {profile.imageDigest}
                  </p>

                  <div className="space-y-1">
                    <p className="text-xs font-semibold text-muted-foreground">
                      Relaxed permissions:
                    </p>
                    <ul className="list-disc list-inside text-xs text-foreground space-y-0.5">
                      {profile.relaxations.map((rel) => (
                        <li key={rel}>{RELAXATION_LABELS[rel] || rel}</li>
                      ))}
                    </ul>
                  </div>

                  <p className="text-xs text-muted-foreground">
                    Allowed by {profile.grantedBy} on {formattedDate}
                  </p>
                </div>

                <div className="shrink-0">
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={disabled || removingDigest === profile.imageDigest}
                    onClick={() => void handleRemove(profile.imageDigest)}
                  >
                    Remove
                  </Button>
                </div>
              </article>
            );
          })}
        </div>
      )}
    </section>
  );
}
