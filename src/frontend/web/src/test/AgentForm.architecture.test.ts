import formSource from '../features/settings/components/AgentForm.tsx?raw';
import connectorsSource from '../features/settings/components/AgentConnectorsEditor.tsx?raw';
import personaSource from '../features/settings/components/AgentPersonaEditor.tsx?raw';
import skillsSource from '../features/settings/components/AgentSkillsEditor.tsx?raw';
import runtimeSource from '../features/settings/components/AgentRuntimeEditor.tsx?raw';
import { describe, expect, it } from 'vitest';

describe('AgentForm responsibility ownership', () => {
  it('keeps persona editing in one focused owner with size ratchets', () => {
    expect(formSource).toContain('<AgentPersonaEditor');
    expect(formSource).toContain('<AgentConnectorsEditor');
    expect(formSource).toContain('<AgentSkillsEditor');
    expect(formSource).toContain('<AgentRuntimeEditor');
    expect(formSource).not.toContain('agent-persona-temperature-');
    expect(formSource).not.toContain('skillAttachments:');
    expect(formSource).not.toContain('mcpBindings: nextBindings');
    expect(personaSource).toContain('agent-persona-temperature-');
    expect(personaSource).toContain('definitions.verbosity_levels.map');
    expect(connectorsSource).toContain('upsertBinding');
    expect(connectorsSource).toContain('onAgentToolModeChange');
    expect(skillsSource).toContain('skillAttachments:');
    expect(runtimeSource).toContain('On HITL expiry');
    expect(runtimeSource).toContain('Tool invocation');
    expect(runtimeSource).toContain('Allow concurrent invocation');
    expect(runtimeSource).toContain('Max parallel tools');
    expect(formSource.split('\n').length).toBeLessThanOrEqual(700);
    expect(personaSource.split('\n').length).toBeLessThanOrEqual(190);
    expect(connectorsSource.split('\n').length).toBeLessThanOrEqual(310);
    expect(skillsSource.split('\n').length).toBeLessThanOrEqual(115);
    expect(runtimeSource.split('\n').length).toBeLessThanOrEqual(360);
  });
});
