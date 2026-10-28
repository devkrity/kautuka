import { Input } from '@/components/ui/input';
import { TabsContent } from '@/components/ui/tabs';
import type { AgentSettingsEntry, PersonaDefinitions } from '../types/settingsTypes';
import { CustomAgentIconField } from './CustomAgentIconField';

interface AgentPersonaEditorProps {
  agent: AgentSettingsEntry;
  definitions: PersonaDefinitions;
  onPatch: (updater: (previous: AgentSettingsEntry) => AgentSettingsEntry) => void;
}

export function AgentPersonaEditor({
  agent,
  definitions,
  onPatch,
}: Readonly<AgentPersonaEditorProps>) {
  return (
    <TabsContent value="persona" className="space-y-3">
      <div className="settings-subpanel-compact">
        <div className="mb-3 text-sm font-medium">Agent identity</div>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <fieldset>
            <legend className="mb-2 text-xs text-muted-foreground">Icon</legend>
            <div className="flex flex-wrap gap-2">
              {['🤖', '🔍', '💻', '📊', '🛡️', '✨', '🧭', '📝'].map((icon) => (
                <button
                  key={icon}
                  type="button"
                  aria-label={`Use ${icon} agent icon`}
                  aria-pressed={agent.persona.icon === icon}
                  onClick={() => onPatch((previous) => ({
                    ...previous,
                    persona: { ...previous.persona, icon },
                  }))}
                  className={`flex h-10 w-10 items-center justify-center rounded-full border text-lg transition ${agent.persona.icon === icon ? 'border-primary ring-2 ring-primary/20' : 'border-border hover:bg-muted'}`}
                >
                  {icon}
                </button>
              ))}
            </div>
            <CustomAgentIconField
              id={`agent-icon-${agent.id}`}
              value={agent.persona.icon ?? ''}
              onChange={(icon) => onPatch((previous) => ({ ...previous, persona: { ...previous.persona, icon } }))}
            />
          </fieldset>
          <fieldset>
            <legend className="mb-2 text-xs text-muted-foreground">Color</legend>
            <div className="flex flex-wrap gap-3">
              {[
                ['#0F766E', 'Teal'], ['#2563EB', 'Blue'], ['#7C3AED', 'Violet'],
                ['#DB2777', 'Pink'], ['#EA580C', 'Orange'], ['#16A34A', 'Green'],
              ].map(([color, label]) => (
                <button
                  key={color}
                  type="button"
                  aria-label={`${label} agent color`}
                  aria-pressed={agent.persona.color === color}
                  onClick={() => onPatch((previous) => ({
                    ...previous,
                    persona: { ...previous.persona, color },
                  }))}
                  className={`h-8 w-8 rounded-full border-2 transition ${agent.persona.color === color ? 'border-foreground ring-2 ring-primary/20' : 'border-transparent'}`}
                  style={{ backgroundColor: color }}
                />
              ))}
            </div>
          </fieldset>
        </div>
        <p className="mt-3 text-xs text-muted-foreground">Used to identify this agent in chat and messages.</p>
      </div>
      <div className="settings-subpanel-compact">
        <div className="mb-3 text-sm font-medium">Persona Profile</div>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-persona-temperature-${agent.id}`}
            >
              Response variety
            </label>
            <Input
              id={`agent-persona-temperature-${agent.id}`}
              type="number"
              min={0}
              max={1}
              step={0.1}
              value={String(agent.persona.temperature)}
              onChange={(event) =>
                onPatch((previous) => ({
                  ...previous,
                  persona: {
                    ...previous.persona,
                    temperature: Math.min(
                      1,
                      Math.max(0, Number.parseFloat(event.target.value) || 0)
                    ),
                  },
                }))
              }
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Lower values keep replies steadier and more predictable. Higher values make them more
              varied and creative. A good starting point is 0.7.
            </p>
          </div>
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-persona-style-${agent.id}`}
            >
              Style
            </label>
            <select
              id={`agent-persona-style-${agent.id}`}
              title="Agent persona style"
              className="input w-full"
              value={agent.persona.style}
              onChange={(event) =>
                onPatch((previous) => ({
                  ...previous,
                  persona: { ...previous.persona, style: event.target.value },
                }))
              }
            >
              {definitions.styles.map((style) => (
                <option key={style} value={style}>
                  {style}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-persona-tone-${agent.id}`}
            >
              Tone
            </label>
            <select
              id={`agent-persona-tone-${agent.id}`}
              title="Agent persona tone"
              className="input w-full"
              value={agent.persona.tone}
              onChange={(event) =>
                onPatch((previous) => ({
                  ...previous,
                  persona: { ...previous.persona, tone: event.target.value },
                }))
              }
            >
              {definitions.tones.map((tone) => (
                <option key={tone} value={tone}>
                  {tone}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-persona-verbosity-${agent.id}`}
            >
              Verbosity
            </label>
            <select
              id={`agent-persona-verbosity-${agent.id}`}
              title="Agent persona verbosity"
              className="input w-full"
              value={agent.persona.verbosity}
              onChange={(event) =>
                onPatch((previous) => ({
                  ...previous,
                  persona: { ...previous.persona, verbosity: event.target.value },
                }))
              }
            >
              {definitions.verbosity_levels.map((verbosity) => (
                <option key={verbosity} value={verbosity}>
                  {verbosity}
                </option>
              ))}
            </select>
          </div>
        </div>
      </div>
    </TabsContent>
  );
}
