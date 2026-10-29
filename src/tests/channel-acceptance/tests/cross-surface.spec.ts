import { expect, test } from '@playwright/test';
import { executeScenario, type AcceptanceExecution } from '../src/harness.js';
import { acceptanceScenarios } from '../src/scenarios.js';

type ExecutionFactory = () => Promise<AcceptanceExecution> | AcceptanceExecution;

const executorModule = process.env.KAUTUKA_ACCEPTANCE_EXECUTOR_MODULE ?? '../src/executor.js';

test.describe.configure({ mode: 'serial' });

test.describe('approved standalone simulator execution', () => {
  for (const scenario of acceptanceScenarios) {
    test(scenario.id, async ({ browser }) => {
      const module = await import(executorModule!);
      const createExecution = module.createAcceptanceExecution as ExecutionFactory | undefined;
      expect(createExecution, 'The executor module must export createAcceptanceExecution.').toBeDefined();
      const execution = await createExecution!();
      expect(execution.stack.reuseExistingStack, 'Normal acceptance scenarios must own a fresh stack.').toBeFalsy();
      await executeScenario(browser, scenario, execution);
    });
  }
});
