import { SegmentedControl } from '@/components/ui/segmented-control';
import { TabsContent } from '@/components/ui/tabs';
import { HitlFunctionStat } from '@/features/approvals';
import type { Skill, SkillAttachmentMode } from '@/features/skills';
import type { AgentSettingsEntry } from '../types/settingsTypes';
import { SkillCard } from './shared/SkillCard';

interface AgentSkillsEditorProps {
  agent: AgentSettingsEntry;
  skills: Skill[];
  selectionEnabled: boolean;
  onPatch: (updater: (previous: AgentSettingsEntry) => AgentSettingsEntry) => void;
}

function modeLabel(mode: SkillAttachmentMode): string {
  if (mode === 'trusted') return 'Trusted';
  if (mode === 'require_approval') return 'Ask first';
  return 'Disabled';
}

export function AgentSkillsEditor({
  agent,
  skills,
  selectionEnabled,
  onPatch,
}: Readonly<AgentSkillsEditorProps>) {
  const getMode = (skillId: string): SkillAttachmentMode =>
    agent.skillAttachments.find((entry) => entry.skillId === skillId)?.mode ?? 'disabled';

  const setMode = (skillId: string, mode: SkillAttachmentMode) => {
    onPatch((previous) => ({
      ...previous,
      skillAttachments: [
        ...previous.skillAttachments.filter((entry) => entry.skillId !== skillId),
        { skillId, mode },
      ],
    }));
  };

  return (
    <TabsContent value="skills">
      <div className="settings-subpanel-compact">
        <div className="mb-3 flex items-start justify-between gap-3">
          <div className="text-sm font-medium">Skills</div>
          <div className="text-xs text-muted-foreground">
            {selectionEnabled
              ? 'Attach imported platform skills to this agent.'
              : 'Skills disabled (sandbox required).'}
          </div>
        </div>
        {skills.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No platform skills imported yet. Import them from the Skills page first.
          </p>
        ) : (
          <div
            className={[
              'space-y-2',
              !selectionEnabled ? 'pointer-events-none opacity-60' : '',
            ].join(' ')}
          >
            {skills.map((skill) => {
              const selectedMode = getMode(skill.id);
              return (
                <SkillCard
                  key={skill.id}
                  skill={skill}
                  extraBadges={
                    <HitlFunctionStat agentId={agent.id} skillId={skill.id} label={skill.name} />
                  }
                  control={
                    <SegmentedControl
                      value={selectedMode}
                      disabled={!selectionEnabled}
                      onChange={(option) => setMode(skill.id, option)}
                      options={(['disabled', 'trusted', 'require_approval'] as const).map((option) => ({
                        value: option,
                        label: modeLabel(option),
                        title: `${modeLabel(option)} for ${skill.name}`,
                        tone:
                          option === 'disabled'
                            ? 'neutral'
                            : option === 'trusted'
                              ? 'primary'
                              : 'attention',
                      }))}
                    />
                  }
                  details={
                    !selectionEnabled ? (
                      <p>Enable sandbox to attach and run skills.</p>
                    ) : skill.hasScripts && selectedMode === 'require_approval' ? (
                      <p>Script execution will enter HITL approval flow.</p>
                    ) : undefined
                  }
                />
              );
            })}
          </div>
        )}
      </div>
    </TabsContent>
  );
}
