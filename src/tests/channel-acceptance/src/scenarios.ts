import { attachTimingScenarios } from './attach-timing.js';
import { baseAcceptanceScenarios } from './base-scenarios.js';
import { browserSmokeScenarios } from './browser-smoke.js';
import { catalogueScenarios } from './catalogue-scenarios.js';
import { naradRuntimeScenarios } from './narad-scenarios.js';
import { naradLegacyAliasScenarios } from './narad-legacy-scenarios.js';
import { deepProviderScenarios } from './deep-provider-scenarios.js';
import { fileWorkflowScenarios } from './file-workflow.js';
import { nestedAaatScenarios } from './nested-aaat.js';
import { operationalFaultScenarios } from './operational-fault-scenarios.js';
import type { AcceptanceScenario } from './model.js';

export const acceptanceScenarios: readonly AcceptanceScenario[] = [
  ...browserSmokeScenarios,
  ...baseAcceptanceScenarios,
  ...naradRuntimeScenarios,
  ...naradLegacyAliasScenarios,
  ...deepProviderScenarios,
  ...nestedAaatScenarios,
  ...attachTimingScenarios,
  ...fileWorkflowScenarios,
  ...operationalFaultScenarios,
  ...catalogueScenarios,
];
