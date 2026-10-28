import React from 'react';
import { Badge } from '@/components/ui/badge';
import type { Skill } from '@/features/skills';

interface SkillCardProps {
  skill: Skill;
  control: React.ReactNode;
  extraBadges?: React.ReactNode;
  details?: React.ReactNode;
}

export function SkillCard({ skill, control, extraBadges, details }: Readonly<SkillCardProps>) {
  const showDetails = Boolean(skill.description) || Boolean(details);

  return (
    <div className="overflow-hidden rounded-xl border border-border/50 bg-card shadow-sm">
      <div className="flex items-center gap-3 px-4 py-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <div className="text-sm font-medium text-foreground">{skill.name}</div>
            {skill.hasResources && <Badge variant="info">Resources</Badge>}
            {skill.hasScripts && <Badge variant="attention">Scripts</Badge>}
            {extraBadges}
          </div>
          <p className="mt-1 text-xs text-muted-foreground">{skill.id}</p>
        </div>

        <div className="ml-auto shrink-0">{control}</div>
      </div>

      {showDetails && (
        <div className="border-t border-border/50 px-4 py-3 text-xs text-muted-foreground">
          {skill.description && <p>{skill.description}</p>}
          {details && <div className={skill.description ? 'mt-2' : ''}>{details}</div>}
        </div>
      )}
    </div>
  );
}
