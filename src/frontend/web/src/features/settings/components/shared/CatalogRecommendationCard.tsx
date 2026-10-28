import { PackageOpen, Plug } from 'lucide-react';
import React from 'react';

import { CatalogCard, CatalogIcon } from '@/components/layout/CatalogCard';
import { Badge } from '@/components/ui/badge';
import { StatePill } from '@/components/ui/state-pill';
import type { CatalogRecommendationItem } from '../../api/settingsAdmin';

type CatalogRecommendationCardProps = {
  recommendation: CatalogRecommendationItem;
  icon: 'connector' | 'skill';
  canImport: boolean;
  isInstalling: boolean;
  onInstall: (recommendation: CatalogRecommendationItem) => void;
};

export const CatalogRecommendationCard: React.FC<CatalogRecommendationCardProps> = ({
  recommendation,
  icon,
  canImport,
  isInstalling,
  onInstall,
}) => {
  const Icon = icon === 'connector' ? Plug : PackageOpen;
  const canPressInstall = canImport && recommendation.canInstall && !isInstalling;

  return (
    <CatalogCard
      icon={
        <CatalogIcon>
          <Icon className="h-5 w-5 text-muted-foreground" />
        </CatalogIcon>
      }
      title={recommendation.title}
      badges={
        <>
          <Badge variant="muted">{recommendation.publisher}</Badge>
          {recommendation.trustKind === 'verified_by_kautuka' ? (
            <Badge variant="info">Verified</Badge>
          ) : null}
        </>
      }
      description={recommendation.summary}
      meta={
        <p className="mt-1 truncate font-mono text-[10px] text-muted-foreground/70">
          {recommendation.id}
        </p>
      }
      footer={
        <>
          <span className="text-xs text-muted-foreground">{recommendation.version}</span>
          {recommendation.isAvailable ? (
            <StatePill active={recommendation.enabled}>
              {recommendation.enabled ? 'Enabled' : 'Imported'}
            </StatePill>
          ) : (
            <StatePill
              active={false}
              disabled={!canPressInstall}
              onClick={() => onInstall(recommendation)}
              title={recommendation.installDisabledReason ?? undefined}
            >
              {isInstalling ? 'Installing…' : recommendation.canInstall ? 'Install' : 'Unavailable'}
            </StatePill>
          )}
        </>
      }
    />
  );
};
