import type { AcceptanceScenario } from './model.js';
import { executeAttachAfterTerminal } from './attach-timing-after-terminal.js';
import { executeAttachBeforeRequest, executeAttachDuringContinuation } from './attach-timing-live.js';
import { executeAttachWhilePaused } from './attach-timing-paused.js';

export const attachTimingScenarios: readonly AcceptanceScenario[] = [
  {
    id: 'CRH-034-ATTACH-BEFORE-REQUEST',
    title: 'browser is attached before an approval-requiring turn and observes it without reload',
    tags: ['full', 'release-gate'],
    checklistIds: ['C-02', 'C-08', 'R-06'],
    async execute(context) {
      await executeAttachBeforeRequest(context.page, context.baseUrl, context.fixtures, context.seed);
    },
  },
  {
    id: 'CRH-034-ATTACH-WHILE-PAUSED',
    title: 'browser attaches to a provider-originated paused run and resolves without forking lifecycle',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['C-07', 'C-08', 'R-06'],
    async execute(context) {
      await context.simulator.reset();
      await executeAttachWhilePaused(
        context.page, context.baseUrl, context.simulator, context.fixtures, 'telegram', context.seed);
    },
  },
  {
    id: 'CRH-034-ATTACH-DURING-CONTINUATION',
    title: 'browser stays attached through channel resolution and continuation without reload',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['C-07', 'C-08', 'R-06'],
    async execute(context) {
      await context.simulator.reset();
      await executeAttachDuringContinuation(
        context.page, context.baseUrl, context.simulator, context.fixtures, 'telegram', context.seed);
    },
  },
  {
    id: 'CRH-034-ATTACH-AFTER-TERMINAL',
    title: 'browser attaches after channel resolution and reconstructs the terminal run without a pending card',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['C-08', 'B-03', 'R-06'],
    async execute(context) {
      await context.simulator.reset();
      await executeAttachAfterTerminal(
        context.page, context.baseUrl, context.simulator, context.fixtures, 'telegram', context.seed);
    },
  },
];
