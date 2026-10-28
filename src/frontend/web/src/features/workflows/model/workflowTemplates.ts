import type { SaveWorkflowRequest } from '../schemas/workflowSchema';

export interface WorkflowTemplate {
  id: string;
  name: string;
  blurb: string;
  governed: boolean;
}

export const workflowTemplates: WorkflowTemplate[] = [
  {
    id: 'payroll',
    name: 'Payroll lodge',
    blurb: 'Check the pay run, get your OK, then record it.',
    governed: true,
  },
  {
    id: 'mortgage',
    name: 'Mortgage decision',
    blurb:
      'Review the application and choose whether to proceed, refer, or decline. A decline ends the run.',
    governed: true,
  },
  {
    id: 'portfolio',
    name: 'Daily portfolio brief',
    blurb: 'Gather the day’s portfolio information and prepare a short summary for you to review.',
    governed: false,
  },
  {
    id: 'psychologist',
    name: 'Family psychologist notes',
    blurb: 'Write notes for one family. Nothing is sent without your OK.',
    governed: true,
  },
  {
    id: 'email-attachments',
    name: 'Read email attachments',
    blurb: 'Find matching mail, read its attachments and pass extracted fields to a second step.',
    governed: true,
  },
];

const scalarString = { type: 'string' } as const;
const scalarNumber = { type: 'number' } as const;
const pickList = (...answers: string[]) => ({ type: 'string', enum: answers }) as const;

function fieldTitle(name: string): string {
  if (name === 'title') return 'Run name';
  if (name === 'lodgeRef') return 'Lodgement reference';
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .replace(/\bId\b/g, 'ID')
    .replace(/^\w/, (letter) => letter.toUpperCase());
}

function objectSchema(
  properties: Record<string, Record<string, unknown>>,
  required: string[]
): Record<string, unknown> {
  return {
    type: 'object',
    required,
    properties: Object.fromEntries(
      Object.entries(properties).map(([name, schema]) => [
        name,
        { ...schema, title: schema.title ?? fieldTitle(name) },
      ])
    ),
  };
}

function pickAgent(agents: string[], index: number): string {
  return agents[Math.min(index, agents.length - 1)] ?? agents[0] ?? '';
}

export function buildWorkflowTemplate(templateId: string, agentIds: string[]): SaveWorkflowRequest {
  const agents = agentIds.filter(Boolean);
  if (agents.length === 0) {
    throw new Error('Create an agent first.');
  }

  switch (templateId) {
    case 'payroll':
      return payroll(agents);
    case 'mortgage':
      return mortgage(agents);
    case 'portfolio':
      return portfolio(agents);
    case 'psychologist':
      return psychologist(agents);
    case 'email-attachments':
      return emailAttachments(agents);
    default:
      throw new Error(`Unknown template '${templateId}'.`);
  }
}

function emailAttachments(agents: string[]): SaveWorkflowRequest {
  const input = objectSchema({ search: scalarString, receivedAfter: scalarString }, ['search']);
  const emailItems = { type: 'array', items: scalarString } as const;
  const working = objectSchema({ items: emailItems }, ['items']);
  return {
    name: 'Read email attachments',
    governed: true,
    inputSchema: input,
    workingSchema: working,
    outputSchema: working,
    nodes: [
      {
        nodeId: 'read-mail',
        agentId: agents[0] ?? '',
        promptTemplate:
          'Find messages matching {{search}} received after {{receivedAfter}}. For each message, record its sender, subject, file name, a short summary, and the fields you can extract.',
        outputKind: 'schema',
        outputSchema: objectSchema({ items: emailItems }, ['items']),
        maps: [{ from: '/items', to: '/items' }],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: true,
        maxRetries: 1,
      },
      {
        nodeId: 'summarize',
        agentId: agents[1] ?? agents[0] ?? '',
        promptTemplate:
          'Summarize the email attachment results in {{items}}. Preserve the source fields and do not invent facts.',
        outputKind: 'schema',
        outputSchema: objectSchema({ items: emailItems }, ['items']),
        maps: [{ from: '/items', to: '/items' }],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: true,
        maxRetries: 1,
      },
    ],
  };
}

function payroll(agents: string[]): SaveWorkflowRequest {
  const input = objectSchema(
    {
      title: scalarString,
      employeeId: scalarString,
      period: scalarString,
      amount: scalarNumber,
    },
    ['title', 'employeeId', 'period', 'amount']
  );
  const working = objectSchema(
    {
      title: scalarString,
      employeeId: scalarString,
      period: scalarString,
      amount: scalarNumber,
      recommendation: scalarString,
      lodgeRef: scalarString,
    },
    ['title', 'employeeId', 'period', 'amount']
  );
  return {
    name: 'Payroll lodge',
    governed: true,
    inputSchema: input,
    workingSchema: working,
    outputSchema: working,
    nodes: [
      {
        nodeId: 'prepare',
        agentId: pickAgent(agents, 0),
        promptTemplate:
          'Check the pay for {{employeeId}} for {{period}}. Say whether to lodge it or hold it, and give a short reason.',
        outputKind: 'schema',
        outputSchema: objectSchema(
          { title: scalarString, recommendation: pickList('lodge', 'hold') },
          ['title', 'recommendation']
        ),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/recommendation', to: '/recommendation' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: true,
        maxRetries: 1,
      },
      {
        nodeId: 'lodge',
        agentId: pickAgent(agents, 1),
        promptTemplate: 'Record the lodgement reference for {{employeeId}} and {{period}}.',
        outputKind: 'schema',
        outputSchema: objectSchema({ title: scalarString, lodgeRef: scalarString }, [
          'title',
          'lodgeRef',
        ]),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/lodgeRef', to: '/lodgeRef' },
        ],
        sideEffects: 'external',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
      },
    ],
  };
}

function mortgage(agents: string[]): SaveWorkflowRequest {
  const input = objectSchema(
    {
      title: scalarString,
      applicant: scalarString,
      amount: scalarNumber,
      creditScore: scalarNumber,
    },
    ['title', 'applicant', 'amount', 'creditScore']
  );
  const working = objectSchema(
    {
      title: scalarString,
      applicant: scalarString,
      amount: scalarNumber,
      creditScore: scalarNumber,
      decision: scalarString,
      outcome: scalarString,
    },
    ['title', 'applicant', 'amount', 'creditScore']
  );
  return {
    name: 'Mortgage decision',
    governed: true,
    inputSchema: input,
    workingSchema: working,
    outputSchema: working,
    nodes: [
      {
        nodeId: 'assess',
        agentId: pickAgent(agents, 0),
        promptTemplate:
          'Review {{applicant}} for a loan of {{amount}} using the credit score {{creditScore}}. Choose proceed, refer, or decline and give a short reason.',
        outputKind: 'schema',
        outputSchema: objectSchema(
          { title: scalarString, decision: pickList('proceed', 'refer', 'decline') },
          ['title', 'decision']
        ),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/decision', to: '/decision' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 1,
        switchCases: [
          { pointer: '/decision', op: 'eq', value: 'proceed', goTo: 'proceed' },
          { pointer: '/decision', op: 'eq', value: 'refer', goTo: 'refer' },
        ],
        defaultGoTo: 'decline',
      },
      {
        nodeId: 'proceed',
        agentId: pickAgent(agents, 1),
        promptTemplate: 'Record that the loan can move ahead in principle for {{applicant}}.',
        outputKind: 'schema',
        outputSchema: objectSchema({ title: scalarString, outcome: scalarString }, [
          'title',
          'outcome',
        ]),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/outcome', to: '/outcome' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: true,
        maxRetries: 0,
      },
      {
        nodeId: 'refer',
        agentId: pickAgent(agents, 1),
        promptTemplate: 'Record that the loan needs a credit review for {{applicant}}.',
        outputKind: 'schema',
        outputSchema: objectSchema({ title: scalarString, outcome: scalarString }, [
          'title',
          'outcome',
        ]),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/outcome', to: '/outcome' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: true,
        maxRetries: 0,
      },
      {
        nodeId: 'decline',
        agentId: pickAgent(agents, 1),
        promptTemplate: 'Record the outcome as declined for {{applicant}}. This ends the run.',
        outputKind: 'schema',
        outputSchema: objectSchema({ title: scalarString, outcome: scalarString }, [
          'title',
          'outcome',
        ]),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/outcome', to: '/outcome' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
      },
    ],
  };
}

function portfolio(agents: string[]): SaveWorkflowRequest {
  const input = objectSchema(
    {
      title: scalarString,
      holdings: scalarString,
    },
    ['title', 'holdings']
  );
  const working = objectSchema(
    {
      title: scalarString,
      holdings: scalarString,
      brief: scalarString,
    },
    ['title', 'holdings']
  );
  return {
    name: 'Daily portfolio brief',
    governed: false,
    inputSchema: input,
    workingSchema: working,
    outputSchema: working,
    nodes: [
      {
        nodeId: 'research',
        agentId: pickAgent(agents, 0),
        promptTemplate:
          'Write a short daily summary of {{holdings}}. Do not suggest placing trades.',
        outputKind: 'schema',
        outputSchema: objectSchema({ title: scalarString, brief: scalarString }, [
          'title',
          'brief',
        ]),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/brief', to: '/brief' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: true,
        maxRetries: 1,
      },
    ],
  };
}

function psychologist(agents: string[]): SaveWorkflowRequest {
  const input = objectSchema(
    {
      title: scalarString,
      familyId: scalarString,
    },
    ['title', 'familyId']
  );
  const working = objectSchema(
    {
      title: scalarString,
      familyId: scalarString,
      notes: scalarString,
    },
    ['title', 'familyId']
  );
  return {
    name: 'Family psychologist notes',
    governed: true,
    inputSchema: input,
    workingSchema: working,
    outputSchema: working,
    nodes: [
      {
        nodeId: 'notes',
        agentId: pickAgent(agents, 0),
        promptTemplate:
          'Write clear session notes for {{familyId}}. Keep information about other families out of the notes.',
        outputKind: 'schema',
        outputSchema: objectSchema({ title: scalarString, notes: scalarString }, [
          'title',
          'notes',
        ]),
        maps: [
          { from: '/title', to: '/title' },
          { from: '/notes', to: '/notes' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: true,
        maxRetries: 1,
      },
    ],
  };
}
