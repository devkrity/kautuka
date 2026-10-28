import React from 'react';
import { BookOpenText, CheckCircle2, Circle, FileCode2, FolderOpenDot, PackageOpen, Trash2 } from 'lucide-react';
import { HitlFunctionStat } from '@/features/approvals';
import { CatalogCard, CatalogIcon } from '@/components/layout/CatalogCard';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { StatePill } from '@/components/ui/state-pill';
import type { Skill } from '../schemas/skillSchema';

interface SkillCardProps {
  skill: Skill;
  deleting: boolean;
  toggling: boolean;
  canManage: boolean;
  onInspect: (skillId: string) => void;
  onDelete: (skill: Skill) => void;
  onToggleEnabled: (skill: Skill, enabled: boolean) => void;
}

export const SkillCard: React.FC<SkillCardProps> = ({
  skill,
  deleting,
  toggling,
  canManage,
  onInspect,
  onDelete,
  onToggleEnabled,
}) => {
  const metadataEntries = Object.entries(skill.sourceMetadata);
  const scopeBadge = skill.scope === 'system' ? 'System' : 'User';

  return (
    <CatalogCard
      icon={
        <CatalogIcon>
          <PackageOpen className="h-5 w-5 text-muted-foreground" />
        </CatalogIcon>
      }
      title={skill.name}
      badges={
        <>
          <Badge variant="muted">{scopeBadge}</Badge>
          <Badge variant="muted">{skill.status}</Badge>
          {skill.hasResources ? <Badge variant="muted">Resources</Badge> : null}
          {skill.hasScripts ? <Badge variant="muted">Scripts</Badge> : null}
        </>
      }
      description={skill.description}
      meta={
        <>
          <p className="mt-1 truncate font-mono text-[10px] text-muted-foreground/70" title={skill.id}>
            {skill.id}
          </p>
          <div className="mt-1">
            <HitlFunctionStat skillId={skill.id} label={skill.name} />
          </div>
        </>
      }
      actions={
        <>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="text-muted-foreground"
            onClick={() => onInspect(skill.id)}
            title="Inspect"
          >
            <BookOpenText className="h-3.5 w-3.5" />
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="text-muted-foreground hover:text-destructive"
            onClick={() => onDelete(skill)}
            disabled={deleting || !canManage}
            title={canManage ? 'Delete' : 'Only admins can delete system skills'}
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </>
      }
      footer={
        <>
          <span className="max-w-[60%] truncate font-mono text-xs text-muted-foreground">{skill.id}</span>
          <StatePill
            active={skill.enabled}
            disabled={toggling || !canManage}
            onClick={() => onToggleEnabled(skill, !skill.enabled)}
            title={canManage ? undefined : 'Only admins can change system skill settings'}
          >
            {skill.enabled ? (
              <>
                <CheckCircle2 className="h-3.5 w-3.5" /> {toggling ? 'Saving...' : 'Enabled'}
              </>
            ) : (
              <>
                <Circle className="h-3.5 w-3.5" /> {toggling ? 'Saving...' : 'Disabled'}
              </>
            )}
          </StatePill>
        </>
      }
    >
      <div className="mt-1 grid gap-3 text-sm text-muted-foreground md:grid-cols-2">
        <div className="rounded-lg border border-border/70 bg-muted/30 p-3">
          <div className="mb-1 flex items-center gap-2 font-medium text-foreground">
            <FolderOpenDot className="h-4 w-4" /> Package
          </div>
          <p className="break-all text-xs">{skill.packagePath}</p>
          <p className="mt-2 text-xs">Imported {skill.importTimestamp || 'unknown time'}</p>
        </div>
        <div className="rounded-lg border border-border/70 bg-muted/30 p-3">
          <div className="mb-1 flex items-center gap-2 font-medium text-foreground">
            <FileCode2 className="h-4 w-4" /> Metadata
          </div>
          <p className="break-all text-xs">Hash {skill.packageHash || 'unavailable'}</p>
          <p className="mt-2 line-clamp-3 break-words text-xs">
            {metadataEntries.length > 0
              ? metadataEntries.map(([key, value]) => `${key}: ${value}`).join(' • ')
              : 'No source metadata'}
          </p>
        </div>
      </div>

    </CatalogCard>
  );
};
