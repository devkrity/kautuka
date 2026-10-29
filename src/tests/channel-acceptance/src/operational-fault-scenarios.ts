import { expect } from '@playwright/test';
import { executeProviderApproval } from './provider-approval-lifecycle.js';
import type { AcceptanceScenario } from './model.js';

export const operationalFaultScenarios: readonly AcceptanceScenario[] = [
  {
    id: 'CRH-039-OPERATIONAL-DISCONNECT',
    title: 'controlled provider disconnect retains one canonical lifecycle evidence trail',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['C-08', 'C-11', 'R-06', 'O-02'],
    async execute(context) {
      await context.simulator.reset();
      await executeProviderApproval(
        context.page, context.baseUrl, context.simulator, context.fixtures, 'telegram', context.seed, 'approve', true);
    },
  },
  {
    id: 'CRH-039-OPERATIONAL-RATE-LIMIT',
    title: 'provider rate-limit fault during a paused run still converges on one canonical decision',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['O-02', 'C-08'],
    async execute(context) {
      await context.simulator.reset();
      await context.simulator.scheduleFault({
        provider: 'telegram', kind: 'rate-limit', at: 'before-outbound',
      });
      await executeProviderApproval(
        context.page, context.baseUrl, context.simulator, context.fixtures, 'telegram', `${context.seed}-rate`, 'approve', false);
      const evidence = await context.simulator.exportEvidence();
      expect(evidence.transcript.length).toBeGreaterThan(0);
    },
  },
  {
    id: 'CRH-039-OPERATIONAL-DUPLICATE',
    title: 'duplicate provider delivery does not fork the canonical approval lifecycle',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['O-02', 'C-03'],
    async execute(context) {
      await context.simulator.reset();
      await context.simulator.scheduleFault({
        provider: 'telegram', kind: 'duplicate', at: 'after-ingress',
      });
      await executeProviderApproval(
        context.page, context.baseUrl, context.simulator, context.fixtures, 'telegram', `${context.seed}-dup`, 'approve', false);
    },
  },
];
