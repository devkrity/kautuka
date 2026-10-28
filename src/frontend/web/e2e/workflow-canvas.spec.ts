import { expect, test, type Page } from '@playwright/test';

const agents = [
  { id: 'reviewer', name: 'Accounts assistant', scope: 'system', ownerUserId: null },
  { id: 'manager', name: 'Manager', scope: 'system', ownerUserId: null },
  { id: 'approver', name: 'Auto-approver', scope: 'system', ownerUserId: null },
  { id: 'filer', name: 'Filing clerk', scope: 'system', ownerUserId: null },
];

function blankDefinition() {
  const now = '2026-10-24T00:00:00Z';
  return {
    id: 'wf-canvas-e2e',
    ownerUserId: 'owner',
    name: 'Invoice approval',
    governed: false,
    revision: 1,
    inputSchema: {
      type: 'object',
      required: ['amount'],
      properties: { amount: { type: 'number' } },
    },
    workingSchema: { type: 'object', properties: { amount: { type: 'number' } } },
    outputSchema: { type: 'object', properties: {} },
    nodes: [],
    createdAt: now,
    updatedAt: now,
    diagram: null,
  };
}

function noStartFieldsDefinition() {
  const definition = blankDefinition();
  return {
    ...definition,
    inputSchema: { type: 'object', properties: {} },
    workingSchema: { type: 'object', properties: {} },
    nodes: [
      {
        nodeId: 'accounts',
        agentId: 'reviewer',
        promptTemplate: 'Check my inbox.',
        outputKind: 'schema',
        outputSchema: { type: 'object', properties: {} },
        maps: [],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [],
        defaultGoTo: null,
      },
    ],
  };
}

function typedResultsDefinition() {
  const definition = blankDefinition();
  return {
    ...definition,
    governed: false,
    inputSchema: {
      type: 'object',
      required: ['invoice'],
      properties: {
        invoice: { type: 'string', title: 'Invoice' },
      },
    },
    workingSchema: { type: 'object', properties: {} },
    nodes: [
      {
        nodeId: 'accounts',
        agentId: 'reviewer',
        promptTemplate: 'Read the invoice.',
        outputKind: 'schema',
        outputSchema: {
          type: 'object',
          properties: {
            amount: { type: 'number' },
            approved: { type: 'boolean' },
            category: { type: 'string', enum: ['Refund', 'Complaint', 'Other'] },
          },
        },
        maps: [
          { from: '/amount', to: '/amount' },
          { from: '/approved', to: '/approved' },
          { from: '/category', to: '/category' },
        ],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [
          { pointer: '/amount', op: 'gt', value: '500', goTo: 'manager' },
          { pointer: '/approved', op: 'eq', value: 'true', goTo: 'manager' },
          { pointer: '/category', op: 'eq', value: 'Refund', goTo: 'manager' },
        ],
        defaultGoTo: 'approver',
      },
      ...['manager', 'approver'].map((nodeId, index) => ({
        nodeId,
        agentId: index === 0 ? 'manager' : 'approver',
        promptTemplate: 'Continue with the invoice.',
        outputKind: 'schema',
        outputSchema: { type: 'object', properties: {} },
        maps: [],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [],
        defaultGoTo: null,
      })),
    ],
  };
}

function plainInvoiceDefinition() {
  const definition = typedResultsDefinition();
  return {
    ...definition,
    nodes: definition.nodes.map((node, index) =>
      index === 0 ? { ...node, switchCases: [node.switchCases[0]!] } : node
    ),
  };
}

function statusCheckDefinition() {
  const definition = typedResultsDefinition();
  return {
    ...definition,
    nodes: definition.nodes.map((node, index) =>
      index === 0
        ? {
            ...node,
            outputSchema: {
              type: 'object',
              properties: { status: { type: 'string', title: 'Status' } },
            },
            maps: [{ from: '/status', to: '/status' }],
            switchCases: [{ pointer: '/status', op: 'eq', value: 'ready', goTo: 'manager' }],
            defaultGoTo: 'approver',
          }
        : { ...node, promptTemplate: 'Continue with {{status}}.' }
    ),
  };
}

function checkSkipsReviewAndJoinsDefinition() {
  const definition = statusCheckDefinition();
  return {
    ...definition,
    nodes: definition.nodes.map((node, index) =>
      index === 1 ? { ...node, requiresReview: true, defaultGoTo: 'approver' } : node
    ),
  };
}

function twoChecksOtherwiseDefinition(name = 'Mortgage decision') {
  const definition = typedResultsDefinition();
  const first = definition.nodes[0]!;
  const second = definition.nodes[1]!;
  const rest = definition.nodes.slice(2);
  return {
    ...definition,
    name,
    nodes: [
      {
        ...first,
        switchCases: first.switchCases.slice(0, 2),
        defaultGoTo: 'filing',
      },
      ...rest,
      {
        nodeId: 'filing',
        agentId: 'filer',
        promptTemplate: 'Record the decision.',
        outputKind: 'schema',
        outputSchema: { type: 'object', properties: {} },
        maps: [],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [],
        defaultGoTo: null,
      },
      second,
    ],
  };
}

function layeredBranchingDefinition() {
  const definition = blankDefinition();
  const terminal = (nodeId: string, agentId: string) => ({
    nodeId,
    agentId,
    promptTemplate: `Finish ${nodeId}.`,
    outputKind: 'schema',
    outputSchema: { type: 'object', properties: {} },
    maps: [],
    sideEffects: 'none',
    memoryWrites: false,
    requiresReview: false,
    maxRetries: 0,
    switchCases: [],
    defaultGoTo: null,
  });
  const branching = (nodeId: string, agentId: string, firstTarget: string, otherwise: string) => ({
    ...terminal(nodeId, agentId),
    outputSchema: {
      type: 'object',
      properties: { decision: { type: 'string', enum: ['first', 'other'] } },
    },
    maps: [{ from: '/decision', to: '/decision' }],
    switchCases: [{ pointer: '/decision', op: 'eq', value: 'first', goTo: firstTarget }],
    defaultGoTo: otherwise,
  });

  return {
    ...definition,
    name: 'Layered branches',
    inputSchema: { type: 'object', properties: {} },
    workingSchema: {
      type: 'object',
      properties: { decision: { type: 'string', enum: ['first', 'other'] } },
    },
    nodes: [
      { ...terminal('setup', 'reviewer'), defaultGoTo: 'branch' },
      branching('branch', 'manager', 'left', 'right'),
      branching('left', 'approver', 'bottom-left', 'bottom-middle'),
      branching('right', 'filer', 'bottom-middle', 'bottom-right'),
      terminal('bottom-left', 'reviewer'),
      terminal('bottom-middle', 'manager'),
      terminal('bottom-right', 'approver'),
    ],
  };
}

function nestedBranchJoinDefinition() {
  const definition = blankDefinition();
  const terminal = (nodeId: string, agentId: string) => ({
    nodeId,
    agentId,
    promptTemplate: `Finish ${nodeId}.`,
    outputKind: 'schema',
    outputSchema: { type: 'object', properties: {} },
    maps: [],
    sideEffects: 'none',
    memoryWrites: false,
    requiresReview: false,
    maxRetries: 0,
    switchCases: [],
    defaultGoTo: null,
  });
  const branching = (
    nodeId: string,
    agentId: string,
    field: string,
    targets: string[],
    otherwise: string | null
  ) => ({
    ...terminal(nodeId, agentId),
    outputSchema: { type: 'object', properties: { [field]: { type: 'string' } } },
    maps: [{ from: `/${field}`, to: `/${field}` }],
    switchCases: targets.map((target, index) => ({
      pointer: `/${field}`,
      op: 'eq',
      value: String(index + 1),
      goTo: target,
    })),
    defaultGoTo: otherwise,
  });

  return {
    ...definition,
    name: 'Rerouted branch join',
    inputSchema: { type: 'object', properties: {} },
    workingSchema: {
      type: 'object',
      properties: { first: { type: 'string' }, second: { type: 'string' } },
    },
    nodes: [
      branching('first', 'reviewer', 'first', ['second'], 'third'),
      terminal('second', 'manager'),
      branching('third', 'approver', 'second', ['fourth', 'fifth'], 'sixth'),
      terminal('fourth', 'filer'),
      terminal('fifth', 'reviewer'),
      terminal('sixth', 'manager'),
    ],
  };
}

function shortBranchJoiningLongerPathsDefinition() {
  const definition = blankDefinition();
  const terminal = (nodeId: string, defaultGoTo: string | null) => ({
    nodeId,
    agentId: 'reviewer',
    promptTemplate: `Finish ${nodeId}.`,
    outputKind: 'schema',
    outputSchema: { type: 'object', properties: {} },
    maps: [],
    sideEffects: 'none',
    memoryWrites: false,
    requiresReview: false,
    maxRetries: 0,
    switchCases: [],
    defaultGoTo,
  });
  return {
    ...definition,
    name: 'Short branch joins longer paths',
    inputSchema: { type: 'object', properties: {} },
    workingSchema: {
      type: 'object',
      properties: { decision: { type: 'string', enum: ['short', 'middle', 'long'] } },
    },
    nodes: [
      {
        ...terminal('check', null),
        promptTemplate: 'Choose a path.',
        outputSchema: {
          type: 'object',
          properties: { decision: { type: 'string', enum: ['short', 'middle', 'long'] } },
        },
        maps: [{ from: '/decision', to: '/decision' }],
        switchCases: [
          { pointer: '/decision', op: 'eq', value: 'short', goTo: 'short' },
          { pointer: '/decision', op: 'eq', value: 'middle', goTo: 'middle' },
        ],
        defaultGoTo: 'long',
      },
      terminal('short', 'join'),
      terminal('middle', 'join'),
      terminal('long', 'longer'),
      terminal('longer', 'join'),
      terminal('join', null),
    ],
  };
}

function mortgageTemplateDefinition() {
  const definition = blankDefinition();
  return {
    ...definition,
    name: 'Mortgage',
    inputSchema: {
      type: 'object',
      required: ['applicant', 'amount', 'creditScore'],
      properties: {
        applicant: { type: 'string', title: 'Applicant' },
        amount: { type: 'number', title: 'Loan amount' },
        creditScore: { type: 'number', title: 'Credit score' },
      },
    },
    workingSchema: {
      type: 'object',
      properties: {
        applicant: { type: 'string', title: 'Applicant' },
        amount: { type: 'number', title: 'Loan amount' },
        creditScore: { type: 'number', title: 'Credit score' },
        decision: { type: 'string', title: 'Decision', enum: ['proceed', 'refer', 'decline'] },
      },
    },
    nodes: [
      {
        nodeId: 'review',
        agentId: 'reviewer',
        promptTemplate: 'Review {{applicant}} for {{amount}} using {{creditScore}}.',
        outputKind: 'schema',
        outputSchema: {
          type: 'object',
          properties: {
            decision: { type: 'string', title: 'Decision', enum: ['proceed', 'refer', 'decline'] },
          },
        },
        maps: [{ from: '/decision', to: '/decision' }],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [
          { pointer: '/decision', op: 'eq', value: 'proceed', goTo: 'proceed' },
          { pointer: '/decision', op: 'eq', value: 'refer', goTo: 'refer' },
        ],
        defaultGoTo: 'decline',
      },
      ...[
        ['proceed', 'Manager', true],
        ['refer', 'Manager', true],
        ['decline', 'Filing clerk', false],
      ].map(([nodeId, agentName, requiresReview]) => ({
        nodeId,
        agentId: agentName === 'Manager' ? 'manager' : 'filer',
        promptTemplate: `Record the ${nodeId} outcome.`,
        outputKind: 'schema',
        outputSchema: { type: 'object', properties: {} },
        maps: [],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview,
        maxRetries: 0,
        switchCases: [],
        defaultGoTo: null,
      })),
    ],
  };
}

function fiveStepChainDefinition() {
  const definition = blankDefinition();
  return {
    ...definition,
    name: 'Five step chain',
    inputSchema: { type: 'object', properties: {} },
    workingSchema: { type: 'object', properties: {} },
    nodes: Array.from({ length: 5 }, (_, index) => ({
      nodeId: `step-${index + 1}`,
      agentId: 'reviewer',
      promptTemplate: `Complete step ${index + 1}.`,
      outputKind: 'schema',
      outputSchema: { type: 'object', properties: {} },
      maps: [],
      sideEffects: 'none',
      memoryWrites: false,
      requiresReview: false,
      maxRetries: 0,
      switchCases: [],
      defaultGoTo: null,
    })),
  };
}

function sixBranchDefinition() {
  const definition = blankDefinition();
  const outcomes = ['ready', 'check', 'missing', 'late', 'duplicate', 'other'];
  const targets = outcomes.map((_, index) => `path-${index + 1}`);
  const terminal = (
    nodeId: string,
    agentId: string,
    promptTemplate: string,
    defaultGoTo: string | null
  ) => ({
    nodeId,
    agentId,
    promptTemplate,
    outputKind: 'schema',
    outputSchema: { type: 'object', properties: {} },
    maps: [],
    sideEffects: 'none',
    memoryWrites: false,
    requiresReview: false,
    maxRetries: 0,
    switchCases: [],
    defaultGoTo,
  });
  return {
    ...definition,
    name: 'Six branch workflow',
    inputSchema: { type: 'object', properties: {} },
    workingSchema: { type: 'object', properties: { outcome: { type: 'string', enum: outcomes } } },
    nodes: [
      {
        ...terminal('branch', 'reviewer', 'Choose the best outcome.', targets[5]!),
        outputSchema: {
          type: 'object',
          properties: { outcome: { type: 'string', enum: outcomes } },
        },
        maps: [{ from: '/outcome', to: '/outcome' }],
        switchCases: outcomes.slice(0, -1).map((value, index) => ({
          pointer: '/outcome',
          op: 'eq',
          value,
          goTo: targets[index],
        })),
      },
      ...targets.map((nodeId, index) =>
        terminal(nodeId, agents[index % agents.length]!.id, `Handle ${outcomes[index]}.`, 'finish')
      ),
      terminal('finish', 'filer', 'Record the chosen outcome.', null),
    ],
  };
}

async function panNodeIntoView(page: Page, nodeId: string) {
  await expect(page.locator('.workflow-canvas-surface')).toBeVisible();
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  const node = page.locator(`.react-flow__node[data-id="${nodeId}"]`);
  await expect(node).toBeAttached();
  const geometry = await page.evaluate((id) => {
    const surface = document.querySelector<HTMLElement>('.workflow-canvas-surface');
    const node = document.querySelector<HTMLElement>(`.react-flow__node[data-id="${id}"]`);
    if (!surface || !node) return null;
    const canvas = surface.getBoundingClientRect();
    const box = node.getBoundingClientRect();
    const margin = innerWidth < 600 ? 2 : 12;
    if (
      box.left >= canvas.left + margin &&
      box.top >= canvas.top + margin &&
      box.right <= canvas.right - margin &&
      box.bottom <= canvas.bottom - margin
    )
      return { visible: true as const };
    const obstacles = Array.from(
      surface.querySelectorAll<HTMLElement>(
        '.react-flow__node, .workflow-canvas-edge-label, .react-flow__controls, .react-flow__panel'
      )
    ).map((element) => element.getBoundingClientRect());
    const points = [0.2, 0.35, 0.5, 0.65, 0.8].flatMap((x) =>
      [0.2, 0.35, 0.5, 0.65, 0.8].map((y) => ({
        x: canvas.left + canvas.width * x,
        y: canvas.top + canvas.height * y,
      }))
    );
    const point = points.find(
      (candidate) =>
        candidate.x > canvas.left + 12 &&
        candidate.x < canvas.right - 12 &&
        candidate.y > canvas.top + 12 &&
        candidate.y < canvas.bottom - 12 &&
        !obstacles.some(
          (obstacle) =>
            candidate.x >= obstacle.left &&
            candidate.x <= obstacle.right &&
            candidate.y >= obstacle.top &&
            candidate.y <= obstacle.bottom
        )
    );
    if (!point) return { visible: false as const, error: 'No open canvas area to pan from.' };
    return {
      visible: false as const,
      point,
      dx: canvas.left + canvas.width / 2 - (box.left + box.width / 2),
      dy: canvas.top + canvas.height / 2 - (box.top + box.height / 2),
    };
  }, nodeId);
  if (geometry?.visible) return;
  expect(geometry, `Could not find the canvas node ${nodeId}`).not.toBeNull();
  if (!geometry) return;
  expect('error' in geometry, 'Expected an open canvas area to start panning').toBe(false);
  if ('error' in geometry) return;
  const point = geometry.point;
  await page.mouse.move(point.x, point.y);
  await page.mouse.down();
  await page.mouse.move(point.x + geometry.dx, point.y + geometry.dy, { steps: 8 });
  await page.mouse.up();
  await expect
    .poll(() =>
      page.evaluate((id) => {
        const surface = document.querySelector<HTMLElement>('.workflow-canvas-surface');
        const node = document.querySelector<HTMLElement>(`.react-flow__node[data-id="${id}"]`);
        if (!surface || !node) return false;
        const canvas = surface.getBoundingClientRect();
        const box = node.getBoundingClientRect();
        const margin = innerWidth < 600 ? 2 : 12;
        return (
          box.left >= canvas.left + margin &&
          box.top >= canvas.top + margin &&
          box.right <= canvas.right - margin &&
          box.bottom <= canvas.bottom - margin
        );
      }, nodeId)
    )
    .toBe(true);
}

async function expectNodesFitOrCanPan(page: Page) {
  await expect(page.locator('.workflow-canvas-surface')).toBeVisible();
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  const offscreenIds = await page.evaluate(() => {
    const surface = document.querySelector<HTMLElement>('.workflow-canvas-surface');
    if (!surface) return ['canvas missing'];
    const canvas = surface.getBoundingClientRect();
    const margin = innerWidth < 600 ? 2 : 12;
    return Array.from(document.querySelectorAll<HTMLElement>('.react-flow__node'))
      .filter((node) => {
        const box = node.getBoundingClientRect();
        return (
          box.left < canvas.left + margin ||
          box.top < canvas.top + margin ||
          box.right > canvas.right - margin ||
          box.bottom > canvas.bottom - margin
        );
      })
      .map((node) => node.dataset.id ?? '');
  });
  if (!offscreenIds.length) return;
  await expectCanvasTextReadable(page);
  for (const id of offscreenIds) await panNodeIntoView(page, id);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
}

async function expectStartAndFirstStepInView(page: Page, firstStepKey: string) {
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  const firstTwoVisible = () =>
    page.evaluate((key) => {
      const surface = document.querySelector<HTMLElement>('.workflow-canvas-surface');
      const start = document.querySelector<HTMLElement>(
        '.react-flow__node[data-id="__workflow_start__"]'
      );
      const first = document.querySelector<HTMLElement>(`.react-flow__node[data-id="${key}"]`);
      if (!surface || !start || !first) return false;
      const bounds = surface.getBoundingClientRect();
      const margin = innerWidth < 600 ? 2 : 12;
      return [start, first].every((node) => {
        const box = node.getBoundingClientRect();
        return (
          box.left >= bounds.left + margin &&
          box.top >= bounds.top + margin &&
          box.right <= bounds.right - margin &&
          box.bottom <= bounds.bottom - margin
        );
      });
    }, firstStepKey);
  await expect.poll(firstTwoVisible).toBe(true);
  const viewport = page.locator('.react-flow__viewport');
  const initialZoom = Number(
    (await viewport.getAttribute('style'))?.match(/scale\(([^)]+)\)/)?.[1] ?? 1
  );
  await page.waitForTimeout(250);
  const settledZoom = Number(
    (await viewport.getAttribute('style'))?.match(/scale\(([^)]+)\)/)?.[1] ?? 1
  );
  expect(settledZoom).toBe(initialZoom);
  await expect.poll(firstTwoVisible).toBe(true);
}

async function expectCanvasWithinViewport(page: Page) {
  const bounds = await page.locator('.workflow-canvas-surface').boundingBox();
  expect(bounds).not.toBeNull();
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(await page.evaluate(() => innerHeight));
  expect(await page.evaluate(() => document.documentElement.scrollHeight <= innerHeight)).toBe(
    true
  );
}

async function expectCanvasContentUnclipped(page: Page) {
  const clipped = await page.evaluate(() => {
    const problems: string[] = [];
    for (const box of document.querySelectorAll<HTMLElement>('.workflow-canvas-box')) {
      if (box.scrollHeight > box.clientHeight + 1) {
        problems.push(
          `${box.getAttribute('aria-label') ?? 'box'} content exceeds its box (${box.scrollHeight}px > ${box.clientHeight}px)`
        );
      }
      for (const text of box.querySelectorAll<HTMLElement>('*')) {
        const style = getComputedStyle(text);
        if (style.textOverflow === 'ellipsis' && text.closest('.workflow-canvas-badges')) {
          problems.push(`${box.getAttribute('aria-label') ?? 'box'} has truncated text`);
        }
      }
    }
    return problems;
  });
  expect(clipped).toEqual([]);
}

async function expectCanvasTextReadable(page: Page) {
  const readDetails = () =>
    page.evaluate(() => {
      const transform =
        document.querySelector<HTMLElement>('.react-flow__viewport')?.getAttribute('style') ?? '';
      const zoom = Number(transform.match(/scale\(([^)]+)\)/)?.[1] ?? 1);
      const sizes = Array.from(
        document.querySelectorAll<HTMLElement>(
          '.workflow-canvas-preview, .workflow-canvas-start-fields, .workflow-canvas-step-heading strong, .workflow-canvas-step-number, .workflow-canvas-results > span, .workflow-canvas-badges span, .workflow-canvas-edge-label'
        )
      )
        .filter((element) => element.innerText.trim() && element.getClientRects().length > 0)
        .map((element) => Number.parseFloat(getComputedStyle(element).fontSize) * zoom);
      const surface = document.querySelector<HTMLElement>('.workflow-canvas-surface');
      return {
        minimum: Math.min(...sizes),
        zoom,
        surface: surface ? [surface.clientWidth, surface.clientHeight] : null,
        nodeBounds: Array.from(document.querySelectorAll<HTMLElement>('.react-flow__node')).map(
          (node) => {
            const bounds = node.getBoundingClientRect();
            return [node.dataset.id, bounds.width, bounds.height];
          }
        ),
      };
    });
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  const details = await readDetails();
  expect(details.minimum, JSON.stringify(details)).toBeGreaterThanOrEqual(10);
}

async function expectCanvasFieldChipsInsideBoxes(page: Page) {
  const overflow = await page.evaluate(() => {
    const issues: string[] = [];
    for (const box of document.querySelectorAll<HTMLElement>('.workflow-canvas-box')) {
      const boxRect = box.getBoundingClientRect();
      for (const chip of box.querySelectorAll<HTMLElement>('.workflow-canvas-results > span')) {
        const rect = chip.getBoundingClientRect();
        if (
          rect.left < boxRect.left + 1 ||
          rect.right > boxRect.right - 1 ||
          rect.top < boxRect.top + 1 ||
          rect.bottom > boxRect.bottom - 1
        ) {
          issues.push(`${box.getAttribute('aria-label')}: ${chip.innerText}`);
        }
      }
    }
    return issues;
  });
  expect(overflow).toEqual([]);
}

async function expectCanvasControlsClearOfBoxes(page: Page) {
  const overlaps = await page.evaluate(() => {
    const boxes = Array.from(document.querySelectorAll<HTMLElement>('.react-flow__node')).map(
      (node) => node.getBoundingClientRect()
    );
    const controls = Array.from(
      document.querySelectorAll<HTMLElement>('.react-flow__controls')
    ).map((control) => ({
      name: control.innerText || control.getAttribute('aria-label'),
      rect: control.getBoundingClientRect(),
    }));
    return controls.flatMap(({ name, rect }) =>
      boxes.some(
        (box) =>
          rect.left < box.right &&
          rect.right > box.left &&
          rect.top < box.bottom &&
          rect.bottom > box.top
      )
        ? [`${name ?? 'canvas control'} covers a step box`]
        : []
    );
  });
  expect(overlaps).toEqual([]);
}

async function expectCanvasLabelsAndLinesInside(page: Page) {
  const outside = await page.evaluate(() => {
    const surface = document.querySelector<HTMLElement>('.workflow-canvas-surface');
    if (!surface) return ['canvas missing'];
    const bounds = surface.getBoundingClientRect();
    const allNodesFit = Array.from(
      document.querySelectorAll<HTMLElement>('.react-flow__node')
    ).every((node) => {
      const rect = node.getBoundingClientRect();
      return (
        rect.left >= bounds.left &&
        rect.top >= bounds.top &&
        rect.right <= bounds.right &&
        rect.bottom <= bounds.bottom
      );
    });
    if (!allNodesFit) return [];
    const margin = innerWidth < 600 ? 2 : 6;
    const isInside = (x: number, y: number) =>
      x >= bounds.left + margin &&
      x <= bounds.right - margin &&
      y >= bounds.top + margin &&
      y <= bounds.bottom - margin;
    const problems: string[] = [];
    for (const label of document.querySelectorAll<HTMLElement>('.workflow-canvas-edge-label')) {
      const rect = label.getBoundingClientRect();
      if (!isInside(rect.left, rect.top) || !isInside(rect.right, rect.bottom)) {
        problems.push(
          `label outside canvas: ${label.innerText} ${rect.left},${rect.top},${rect.right},${rect.bottom} canvas ${bounds.left},${bounds.top},${bounds.right},${bounds.bottom}`
        );
      }
    }
    for (const path of document.querySelectorAll<SVGPathElement>(
      '.react-flow__edge[data-id] path'
    )) {
      const matrix = path.getScreenCTM();
      if (!matrix) continue;
      const length = path.getTotalLength();
      for (let offset = 0; offset <= length; offset += Math.max(1, length / 32)) {
        const point = new DOMPoint(
          path.getPointAtLength(offset).x,
          path.getPointAtLength(offset).y
        ).matrixTransform(matrix);
        if (!isInside(point.x, point.y)) {
          problems.push(
            `line outside canvas: ${path.closest<HTMLElement>('.react-flow__edge')?.dataset.id}`
          );
          break;
        }
      }
    }
    return problems;
  });
  expect(outside).toEqual([]);
}

async function expectLabelsNearTheirArrows(page: Page) {
  const distances = await page.evaluate(() =>
    Array.from(document.querySelectorAll<HTMLElement>('.workflow-canvas-edge-label'))
      .filter((label) => label.innerText.trim())
      .map((label) => {
        const edgeId = label.dataset.edgeId;
        const rect = label.getBoundingClientRect();
        const paths = Array.from(
          document.querySelectorAll<SVGPathElement>('.react-flow__edge[data-id] path')
        ).map((path) => {
          if (!path.getScreenCTM())
            return {
              id: path.closest<HTMLElement>('.react-flow__edge')?.dataset.id,
              distance: Infinity,
            };
          const matrix = path.getScreenCTM()!;
          const length = path.getTotalLength();
          let distance = Infinity;
          for (let offset = 0; offset <= length; offset += Math.max(1, length / 100)) {
            const point = new DOMPoint(
              path.getPointAtLength(offset).x,
              path.getPointAtLength(offset).y
            ).matrixTransform(matrix);
            const dx = Math.max(rect.left - point.x, 0, point.x - rect.right);
            const dy = Math.max(rect.top - point.y, 0, point.y - rect.bottom);
            distance = Math.min(distance, Math.hypot(dx, dy));
          }
          return { id: path.closest<HTMLElement>('.react-flow__edge')?.dataset.id, distance };
        });
        const own = paths.find((path) => path.id === edgeId)?.distance ?? Infinity;
        const nearestOtherPath = paths
          .filter((path) => path.id !== edgeId)
          .sort((left, right) => left.distance - right.distance)[0];
        return {
          edgeId,
          label: label.innerText,
          bounds: [rect.left, rect.top, rect.width, rect.height],
          distance: own,
          nearestOther: nearestOtherPath?.distance ?? Infinity,
          nearestOtherEdge: nearestOtherPath?.id,
        };
      })
  );
  expect(distances).toEqual(
    distances.filter((item) => item.distance <= 40 && item.distance + 1 < item.nearestOther)
  );
}

async function expectStartCenteredAndSiblingPathsSeparated(page: Page) {
  await expect
    .poll(async () => {
      const result = await page.evaluate(() => {
        const start = document.querySelector<HTMLElement>(
          '.react-flow__node[data-id="__workflow_start__"]'
        );
        const firstStep = Array.from(
          document.querySelectorAll<HTMLElement>('.react-flow__node')
        ).find((node) => node.dataset.id !== '__workflow_start__');
        if (!start || !firstStep)
          return { alignmentError: Infinity, overlaps: ['Start or Step 1 missing'] };
        const startRect = start.getBoundingClientRect();
        const firstRect = firstStep.getBoundingClientRect();
        const alignmentError = Math.abs(
          startRect.left + startRect.width / 2 - (firstRect.left + firstRect.width / 2)
        );
        const groups = new Map<
          string,
          Array<{ edgeId: string; segments: Array<[number, number, number]> }>
        >();
        for (const label of document.querySelectorAll<HTMLElement>('.workflow-canvas-edge-label')) {
          const source = label.dataset.sourceStep;
          const edgeId = label.dataset.edgeId;
          const path = edgeId
            ? document.querySelector<SVGPathElement>(
                `.react-flow__edge[data-id="${CSS.escape(edgeId)}"] path`
              )
            : null;
          if (!source || !path) continue;
          const commands = path.getAttribute('d')?.match(/[MLQ][^MLQ]*/g) ?? [];
          let current: [number, number] | null = null;
          const segments: Array<[number, number, number]> = [];
          for (const command of commands) {
            const values =
              command
                .slice(1)
                .match(/-?(?:\d+\.?\d*|\.\d+)(?:e[-+]?\d+)?/gi)
                ?.map(Number) ?? [];
            if (command[0] === 'M' && values.length >= 2) current = [values[0]!, values[1]!];
            else if (command[0] === 'L' && current && values.length >= 2) {
              const next: [number, number] = [values[0]!, values[1]!];
              if (Math.abs(current[1] - next[1]) < 0.5) {
                segments.push([
                  Math.min(current[0], next[0]),
                  Math.max(current[0], next[0]),
                  current[1],
                ]);
              }
              current = next;
            } else if (command[0] === 'Q' && values.length >= 4) {
              current = [values[2]!, values[3]!];
            }
          }
          const edges = groups.get(source) ?? [];
          edges.push({ edgeId, segments });
          groups.set(source, edges);
        }
        const overlaps: string[] = [];
        for (const [source, edges] of groups) {
          for (let left = 0; left < edges.length; left += 1) {
            for (let right = left + 1; right < edges.length; right += 1) {
              for (const a of edges[left]!.segments) {
                for (const b of edges[right]!.segments) {
                  if (
                    Math.abs(a[2] - b[2]) < 0.5 &&
                    Math.min(a[1], b[1]) - Math.max(a[0], b[0]) > 1
                  ) {
                    overlaps.push(`${source}: ${edges[left]!.edgeId} and ${edges[right]!.edgeId}`);
                  }
                }
              }
            }
          }
        }
        return { alignmentError, overlaps };
      });
      return result.alignmentError <= 2 && result.overlaps.length === 0;
    })
    .toBe(true);
}

async function expectBranchDestinationsFollowPathOrder(page: Page) {
  const violations = await page.evaluate(() => {
    const nodes = new Map(
      Array.from(document.querySelectorAll<HTMLElement>('.react-flow__node')).map((node) => [
        node.dataset.id,
        node.getBoundingClientRect(),
      ])
    );
    const bySource = new Map<string, string[]>();
    const sourcesByTarget = new Map<string, Set<string>>();
    for (const label of document.querySelectorAll<HTMLElement>('.workflow-canvas-edge-label')) {
      const source = label.dataset.sourceStep;
      const target = label.dataset.targetStep;
      if (!source || !target) continue;
      const sources = sourcesByTarget.get(target) ?? new Set<string>();
      sources.add(source);
      sourcesByTarget.set(target, sources);
      const targets = bySource.get(source) ?? [];
      if (!targets.includes(target)) targets.push(target);
      bySource.set(source, targets);
    }

    const issues: string[] = [];
    for (const [source, targets] of bySource) {
      const independentlyPositionedTargets = targets.filter(
        (target) => sourcesByTarget.get(target)?.size === 1
      );
      const ranks = new Map<number, Array<{ target: string; x: number }>>();
      for (const target of independentlyPositionedTargets) {
        const bounds = nodes.get(target);
        if (!bounds) continue;
        const rank = Math.round((bounds.top + bounds.height / 2) / 4);
        const row = ranks.get(rank) ?? [];
        row.push({ target, x: bounds.left + bounds.width / 2 });
        ranks.set(rank, row);
      }
      for (const [rank, row] of ranks) {
        if (row.length < 2) continue;
        const outOfOrder = row.some((item, index) => index > 0 && item.x <= row[index - 1]!.x);
        if (outOfOrder)
          issues.push(`${source} has branch destinations out of path order on rank ${rank}`);
      }
    }
    return issues;
  });
  expect(violations).toEqual([]);
}

async function expectPathsAvoidUnrelatedBoxes(page: Page) {
  const collisions = await page.evaluate(() => {
    const nodes = Array.from(document.querySelectorAll<HTMLElement>('.react-flow__node')).map(
      (node) => ({ id: node.dataset.id, bounds: node.getBoundingClientRect() })
    );
    const issues: string[] = [];
    const routes: Array<{ source: string; target: string; points: DOMPoint[] }> = [];
    for (const label of document.querySelectorAll<HTMLElement>('.workflow-canvas-edge-label')) {
      const edgeId = label.dataset.edgeId;
      const source = label.dataset.sourceStep;
      const target = label.dataset.targetStep;
      const path = edgeId
        ? document.querySelector<SVGPathElement>(
            `.react-flow__edge[data-id="${CSS.escape(edgeId)}"] path`
          )
        : null;
      if (!path || !source || !target || !path.getScreenCTM()) continue;
      const matrix = path.getScreenCTM()!;
      const length = path.getTotalLength();
      const points: DOMPoint[] = [];
      for (let offset = 0; offset <= length; offset += Math.max(1, length / 160)) {
        const point = new DOMPoint(
          path.getPointAtLength(offset).x,
          path.getPointAtLength(offset).y
        ).matrixTransform(matrix);
        points.push(point);
        const hit = nodes.find(
          (node) =>
            node.id !== source &&
            node.id !== target &&
            point.x > node.bounds.left + 1 &&
            point.x < node.bounds.right - 1 &&
            point.y > node.bounds.top + 1 &&
            point.y < node.bounds.bottom - 1
        );
        if (hit) {
          issues.push(`${source} to ${target} passes through ${hit.id}`);
          break;
        }
      }
      routes.push({ source, target, points });
    }

    const orientation = (a: DOMPoint, b: DOMPoint, c: DOMPoint) =>
      (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    const segmentsCross = (a: DOMPoint, b: DOMPoint, c: DOMPoint, d: DOMPoint) => {
      const abC = orientation(a, b, c);
      const abD = orientation(a, b, d);
      const cdA = orientation(c, d, a);
      const cdB = orientation(c, d, b);
      if (abC * abD < -0.25 && cdA * cdB < -0.25) return true;
      const horizontalOverlap =
        Math.abs(a.y - b.y) < 0.5 &&
        Math.abs(c.y - d.y) < 0.5 &&
        Math.abs(a.y - c.y) < 0.5 &&
        Math.min(Math.max(a.x, b.x), Math.max(c.x, d.x)) -
          Math.max(Math.min(a.x, b.x), Math.min(c.x, d.x)) >
          2;
      const verticalOverlap =
        Math.abs(a.x - b.x) < 0.5 &&
        Math.abs(c.x - d.x) < 0.5 &&
        Math.abs(a.x - c.x) < 0.5 &&
        Math.min(Math.max(a.y, b.y), Math.max(c.y, d.y)) -
          Math.max(Math.min(a.y, b.y), Math.min(c.y, d.y)) >
          2;
      return horizontalOverlap || verticalOverlap;
    };
    for (let left = 0; left < routes.length; left += 1) {
      for (let right = left + 1; right < routes.length; right += 1) {
        const a = routes[left]!;
        const b = routes[right]!;
        if (
          a.source === b.source ||
          a.source === b.target ||
          a.target === b.source ||
          a.target === b.target
        )
          continue;
        let crossed = false;
        for (let i = 1; i < a.points.length && !crossed; i += 1) {
          for (let j = 1; j < b.points.length; j += 1) {
            if (segmentsCross(a.points[i - 1]!, a.points[i]!, b.points[j - 1]!, b.points[j]!)) {
              crossed = true;
              break;
            }
          }
        }
        if (crossed) issues.push(`${a.source} to ${a.target} crosses ${b.source} to ${b.target}`);
      }
    }
    return issues;
  });
  expect(collisions).toEqual([]);
}

async function expectEdgeArrowsToMeetTheirTargetBoxes(page: Page) {
  const detached = await page.evaluate(() =>
    Array.from(document.querySelectorAll<HTMLElement>('.workflow-canvas-edge-label')).flatMap(
      (label) => {
        const edgeId = label.dataset.edgeId;
        const targetId = label.dataset.targetStep;
        if (!edgeId || !targetId) return [];
        const path = document.querySelector<SVGPathElement>(
          `.react-flow__edge[data-id="${CSS.escape(edgeId)}"] path`
        );
        const target = document.querySelector<HTMLElement>(
          `.react-flow__node[data-id="${CSS.escape(targetId)}"]`
        );
        const matrix = path?.getScreenCTM();
        if (!path || !target || !matrix) return [`${edgeId} has no target path or box`];
        const endpoint = path.getPointAtLength(path.getTotalLength()).matrixTransform(matrix);
        const bounds = target.getBoundingClientRect();
        return endpoint.x >= bounds.left - 2 &&
          endpoint.x <= bounds.right + 2 &&
          endpoint.y >= bounds.top - 2 &&
          endpoint.y <= bounds.top + 8
          ? []
          : [
              `${edgeId} ends at ${endpoint.x},${endpoint.y}, away from ${targetId} at ${bounds.left},${bounds.top},${bounds.right},${bounds.bottom}`,
            ];
      }
    )
  );
  expect(detached).toEqual([]);
}

async function expectEdgeLabelsNotToOverlap(page: Page) {
  const overlaps = await page.evaluate(() => {
    const labels = Array.from(
      document.querySelectorAll<HTMLElement>('.workflow-canvas-edge-label'),
      (element) => ({ element, rect: element.getBoundingClientRect(), text: element.innerText })
    );
    const nodes = Array.from(
      document.querySelectorAll<HTMLElement>('.react-flow__node'),
      (element) => ({ rect: element.getBoundingClientRect(), text: element.innerText })
    );
    const overlaps = (a: DOMRect, b: DOMRect) =>
      a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
    const problems: string[] = [];
    for (let index = 0; index < labels.length; index += 1) {
      const label = labels[index]!;
      for (const node of nodes) {
        if (overlaps(label.rect, node.rect))
          problems.push(
            `label ${label.text} (${label.element.dataset.sourceStep} to ${label.element.dataset.targetStep}: ${label.rect.left.toFixed(0)},${label.rect.top.toFixed(0)} ${label.rect.width.toFixed(0)}x${label.rect.height.toFixed(0)}) overlaps node ${node.text} (${node.rect.left.toFixed(0)},${node.rect.top.toFixed(0)} ${node.rect.width.toFixed(0)}x${node.rect.height.toFixed(0)})`
          );
      }
      for (let otherIndex = index + 1; otherIndex < labels.length; otherIndex += 1) {
        const other = labels[otherIndex]!;
        if (overlaps(label.rect, other.rect))
          problems.push(
            `label ${label.text} (${label.element.dataset.edgeId} ${label.rect.left.toFixed(0)},${label.rect.top.toFixed(0)} ${label.rect.width.toFixed(0)}x${label.rect.height.toFixed(0)}) overlaps label ${other.text} (${other.element.dataset.edgeId} ${other.rect.left.toFixed(0)},${other.rect.top.toFixed(0)} ${other.rect.width.toFixed(0)}x${other.rect.height.toFixed(0)})`
          );
      }
      const plus = label.element.querySelector<HTMLElement>('button');
      if (!plus || getComputedStyle(plus.parentElement!).opacity === '0') continue;
      const plusRect = plus.getBoundingClientRect();
      for (let otherIndex = 0; otherIndex < labels.length; otherIndex += 1) {
        if (otherIndex === index) continue;
        const other = labels[otherIndex]!;
        if (overlaps(plusRect, other.rect))
          problems.push(
            `plus (${label.element.dataset.edgeId} ${plusRect.left.toFixed(0)},${plusRect.top.toFixed(0)} ${plusRect.width.toFixed(0)}x${plusRect.height.toFixed(0)}) overlaps label ${other.text} (${other.element.dataset.edgeId} ${other.rect.left.toFixed(0)},${other.rect.top.toFixed(0)} ${other.rect.width.toFixed(0)}x${other.rect.height.toFixed(0)})`
          );
      }
      for (const node of nodes) {
        if (overlaps(plusRect, node.rect)) problems.push(`plus overlaps node ${node.text}`);
      }
      for (let otherIndex = index + 1; otherIndex < labels.length; otherIndex += 1) {
        const otherPlus = labels[otherIndex]!.element.querySelector<HTMLElement>('button');
        if (otherPlus && overlaps(plusRect, otherPlus.getBoundingClientRect())) {
          problems.push('plus overlaps plus');
        }
      }
    }
    return problems;
  });
  expect(overlaps).toEqual([]);
}

function governedMissingResultsDefinition() {
  const definition = blankDefinition();
  return {
    ...definition,
    governed: true,
    inputSchema: { type: 'object', properties: {} },
    workingSchema: { type: 'object', properties: {} },
    nodes: [
      {
        nodeId: 'first',
        agentId: 'reviewer',
        promptTemplate: 'Check the invoice.',
        outputKind: 'schema',
        outputSchema: { type: 'object', properties: { checked: { type: 'boolean' } } },
        maps: [{ from: '/checked', to: '/checked' }],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [],
        defaultGoTo: 'empty',
      },
      {
        nodeId: 'empty',
        agentId: 'reviewer',
        promptTemplate: 'Finish the review.',
        outputKind: 'schema',
        outputSchema: { type: 'object', properties: {} },
        maps: [],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [],
        defaultGoTo: null,
      },
    ],
  };
}

function runFieldsDefinition() {
  const definition = typedResultsDefinition();
  return {
    ...definition,
    inputSchema: {
      type: 'object',
      required: ['invoice'],
      properties: {
        invoice: { type: 'string', title: 'Invoice' },
        category: { type: 'string', enum: ['Refund', 'Complaint'], title: 'Category' },
        approved: { type: 'boolean', title: 'Approved' },
      },
    },
  };
}

async function openEditor(
  page: Page,
  initialDefinition = blankDefinition(),
  uploadMode: 'success' | 'conflict-once' | 'disabled' = 'success',
  runCount = 0,
  runStatus = 'finished',
  pendingApprovals: Array<Record<string, unknown>> = []
) {
  let definition = initialDefinition;
  let hasRuns = runCount > 0;
  let startRequest: Record<string, unknown> | null = null;
  const uploadRequests: string[] = [];
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path.endsWith('/auth/setup/status')) await route.fulfill({ json: { needsSetup: false } });
    else if (path.endsWith('/auth/refresh'))
      await route.fulfill({ json: { accessToken: 'e2e-token', expiresInSeconds: 3600 } });
    else if (path.endsWith('/auth/me'))
      await route.fulfill({ json: { id: 'owner', role: 'Admin', userName: 'Owner' } });
    else if (path.endsWith('/settings/agents')) await route.fulfill({ json: agents });
    else if (path.endsWith('/agents/reviewer/files') && route.request().method() === 'POST') {
      const body = route.request().postDataBuffer()?.toString('utf8') ?? '';
      uploadRequests.push(body);
      if (uploadMode === 'disabled') {
        await route.fulfill({
          status: 409,
          json: { agentId: 'reviewer', reasonCode: 'sandbox_disabled', message: 'Files are off.' },
        });
      } else if (uploadMode === 'conflict-once' && /name="overwrite"\r\n\r\nfalse/.test(body)) {
        uploadMode = 'success';
        await route.fulfill({
          status: 409,
          json: { message: 'A file with that name already exists.' },
        });
      } else {
        await route.fulfill({
          json: {
            agentId: 'reviewer',
            path: 'invoice.pdf',
            fileName: 'invoice.pdf',
            contentType: 'application/pdf',
            sizeBytes: 10,
          },
        });
      }
    } else if (path.endsWith('/approvals/pending')) await route.fulfill({ json: pendingApprovals });
    else if (path.endsWith('/runtime/execution-readiness'))
      await route.fulfill({
        json: { ready: true, code: 'READY', message: 'Ready', dependencies: [] },
      });
    else if (
      path.endsWith('/workflows/wf-canvas-e2e/steps/accounts/test') &&
      route.request().method() === 'POST'
    )
      await route.fulfill({
        json: {
          results: { amount: 520, approved: true, category: 'Refund' },
          error: null,
          droppedKeys: [],
        },
      });
    else if (
      path.endsWith('/workflows/wf-canvas-e2e/instances') &&
      route.request().method() === 'POST'
    ) {
      startRequest = route.request().postDataJSON() as Record<string, unknown>;
      hasRuns = true;
      const now = new Date().toISOString();
      await route.fulfill({
        json: {
          id: 'run-1',
          workflowId: 'wf-canvas-e2e',
          definitionRevision: definition.revision,
          ownerUserId: 'owner',
          correlationKey: startRequest.correlationKey ?? null,
          status: 'Queued',
          stopRequested: false,
          envelope: (startRequest.input as Record<string, unknown>) ?? {},
          envelopeHash: 'hash',
          envelopeRevision: 1,
          snapshot: definition,
          steps: [],
          createdAt: now,
          updatedAt: now,
        },
      });
    } else if (path.endsWith('/workflows/wf-canvas-e2e/instances')) {
      const now = new Date().toISOString();
      await route.fulfill({
        json: Array.from({ length: runCount }, (_, index) => ({
          id: `run-${index + 1}`,
          workflowId: 'wf-canvas-e2e',
          definitionRevision: definition.revision,
          ownerUserId: 'owner',
          status: runStatus,
          stopRequested: false,
          envelope: {},
          envelopeHash: 'hash',
          envelopeRevision: 1,
          snapshot: definition,
          steps: [],
          createdAt: new Date(Date.now() - index * 60_000).toISOString(),
          updatedAt: now,
        })),
      });
    } else if (path.endsWith('/workflows/wf-canvas-e2e/instances/run-1')) {
      const now = new Date().toISOString();
      await route.fulfill({
        json: {
          id: 'run-1',
          workflowId: 'wf-canvas-e2e',
          definitionRevision: definition.revision,
          ownerUserId: 'owner',
          correlationKey: null,
          status: runStatus,
          stopRequested: false,
          envelope: {},
          envelopeHash: 'hash',
          envelopeRevision: 1,
          snapshot: definition,
          steps: [],
          createdAt: now,
          updatedAt: now,
        },
      });
    } else if (path.endsWith('/workflows/wf-canvas-e2e') && route.request().method() === 'PUT') {
      const request = route.request().postDataJSON() as Record<string, unknown>;
      definition = {
        ...definition,
        ...request,
        revision: Number(definition.revision) + 1,
        updatedAt: new Date().toISOString(),
      };
      await route.fulfill({ json: definition });
    } else if (path.endsWith('/workflows/wf-canvas-e2e')) await route.fulfill({ json: definition });
    else if (path.endsWith('/workflows') && url.searchParams.get('archived') === 'true')
      await route.fulfill({ json: [] });
    else if (path.endsWith('/workflows'))
      await route.fulfill({ json: [{ ...definition, hasRuns }] });
    else await route.fulfill({ json: {} });
  });
  await page.goto('/workflows/wf-canvas-e2e/edit');
  if (await page.evaluate(() => innerWidth >= 1024)) {
    await expect(page.getByRole('button', { name: 'Undo', exact: true })).toBeDisabled();
  }
  await expect(page.getByText('Unsaved changes')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Show workflows' })).toBeVisible();
  await expect(page.locator('.workflow-canvas-settings')).toBeHidden();
  await expect(page.locator('.workflow-canvas-surface')).toBeVisible();
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-node-count',
    String(initialDefinition.nodes.length + 1)
  );
  if (await page.evaluate(() => innerWidth >= 1024)) {
    expect(
      (await page.locator('.workflow-canvas-surface').boundingBox())?.width
    ).toBeGreaterThanOrEqual(800);
  }
  return {
    getDefinition: () => definition,
    getStartRequest: () => startRequest,
    getUploadRequests: () => uploadRequests,
  };
}

async function addFirstStep(page: Page) {
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByLabel('Who does the first step?').selectOption('reviewer');
  await page.getByRole('button', { name: 'Add first step' }).click();
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  await page.getByRole('button', { name: 'Close settings' }).click();
  await expect(
    page.getByRole('button', { name: 'Step 1 · Accounts assistant', exact: true })
  ).toBeVisible();
}

test('workflow list opens a read-only overview, and Back from it never asks about edits', async ({
  page,
}) => {
  await openEditor(page);
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 0 steps/ }).click();
  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e$/);
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save workflow' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Edit workflow' }).click();
  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e\/edit$/);
  await expect(page.getByRole('button', { name: 'Save workflow' })).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e$/);
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeVisible();
  await expect(page.getByText('Leave without saving?')).toHaveCount(0);
});

test('overview shows the canvas first and does not repeat the last run', async ({ page }) => {
  await openEditor(page, plainInvoiceDefinition(), 'success', 14);
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  const canvas = page.locator('.workflow-canvas-surface');
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    await expect(canvas).toBeVisible();
    await expectCanvasWithinViewport(page);
    await expect(canvas).toHaveAttribute('data-layout-ready', 'true');
    await expect(page.locator('.react-flow__node')).toHaveCount(4);
    const bounds = await canvas.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height);
    await expectCanvasTextReadable(page);
    await expect(page.locator('.react-flow__controls')).toBeVisible();
    expect(
      await page.evaluate(() => document.documentElement.scrollHeight <= window.innerHeight)
    ).toBe(true);
  }
  await expectEdgeLabelsNotToOverlap(page);
  await expectLabelsNearTheirArrows(page);
  await expect(page.getByRole('region', { name: 'Workflow runs' }).getByRole('link')).toHaveCount(
    5
  );
  await expect(page.getByRole('button', { name: 'Autopilot' })).toBeVisible();
});

test('run pages and workflow lists use matching run status words', async ({ page }) => {
  const now = new Date().toISOString();
  const pendingReview = {
    id: 'review-1',
    toolCallId: 'run-1:accounts:1',
    toolName: 'workflow_review',
    toolType: 'workflow_review',
    requestedAt: now,
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
    kind: 'workflow_review',
    workflowId: 'wf-canvas-e2e',
    instanceId: 'run-1',
    nodeId: 'accounts',
    remainingChangeRequests: 2,
    actions: [
      { label: 'Approve', value: 'approve', style: 'primary' },
      { label: 'Stop run', value: 'stop_run', style: 'danger' },
    ],
  };
  for (const [status, expected, approvals] of [
    ['paused', 'Paused', []],
    ['paused', 'Waiting for your review', [pendingReview]],
    ['finished', 'Finished', []],
    ['error', 'Failed', []],
  ] as const) {
    await openEditor(page, plainInvoiceDefinition(), 'success', 1, status, [...approvals]);
    await page.goto('/workflows');
    await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
    await expect(page.getByText(expected, { selector: '.badge' }).first()).toBeVisible();
    await page.goto('/workflows/wf-canvas-e2e/instances/run-1');
    await expect(page.getByText(expected, { selector: '.badge' }).first()).toBeVisible();
  }
});

test('Mortgage and five-step overviews fit every box and refit when opened in the editor', async ({
  page,
}) => {
  for (const [definition, name, steps] of [
    [mortgageTemplateDefinition(), 'Mortgage', 4],
    [fiveStepChainDefinition(), 'Five step chain', 5],
  ] as const) {
    await openEditor(page, definition);
    await page.goto('/workflows');
    await page.getByRole('button', { name: new RegExp(`${name} ${steps} steps`) }).click();
    for (const viewport of [
      { width: 1280, height: 800 },
      { width: 1440, height: 900 },
    ]) {
      await page.setViewportSize(viewport);
      await expectNodesFitOrCanPan(page);
      await expect(page.locator('.workflow-canvas-surface')).toBeVisible();
      await expectCanvasWithinViewport(page);
      await expectCanvasContentUnclipped(page);
      await expectCanvasTextReadable(page);
      if (name === 'Mortgage') {
        await expect(page.getByText('Waits for your OK').first()).toBeVisible();
        await expectEdgeLabelsNotToOverlap(page);
        await expectLabelsNearTheirArrows(page);
      }
      const clippedBoxes = await page
        .locator('.workflow-canvas-box')
        .evaluateAll((boxes) =>
          boxes
            .filter((box) => (box as HTMLElement).scrollHeight > (box as HTMLElement).clientHeight)
            .map((box) => (box as HTMLElement).getAttribute('aria-label'))
        );
      expect(clippedBoxes).toEqual([]);
      await page.getByRole('button', { name: 'Edit workflow' }).click();
      await expectNodesFitOrCanPan(page);
      await expectCanvasWithinViewport(page);
      await expectCanvasContentUnclipped(page);
      await expectCanvasTextReadable(page);
      await expectEdgeLabelsNotToOverlap(page);
      await expectLabelsNearTheirArrows(page);
      await page.goto('/workflows');
      await page.getByRole('button', { name: new RegExp(`${name} ${steps} steps`) }).click();
    }
  }
});

test('Start is centred and branches leave on separate lines in both views', async ({ page }) => {
  for (const [definition, name, steps] of [
    [plainInvoiceDefinition(), 'Invoice approval', 3],
    [mortgageTemplateDefinition(), 'Mortgage', 4],
  ] as const) {
    await openEditor(page, definition);
    for (const viewport of [
      { width: 1280, height: 800 },
      { width: 1440, height: 900 },
    ]) {
      await page.setViewportSize(viewport);
      await expectStartCenteredAndSiblingPathsSeparated(page);
      await expectCanvasTextReadable(page);
      await page.goto('/workflows');
      await page.getByRole('button', { name: new RegExp(`${name} ${steps} steps`) }).click();
      await expect(page.locator('.react-flow__node[data-id="__workflow_start__"]')).toBeVisible();
      await expectStartCenteredAndSiblingPathsSeparated(page);
      await expectCanvasContentUnclipped(page);
      await expectCanvasTextReadable(page);
      await page.getByRole('button', { name: 'Edit workflow' }).click();
    }
  }
});

test('plain invoice edge labels do not overlap each other or add buttons', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openEditor(page, plainInvoiceDefinition());
  await expectEdgeLabelsNotToOverlap(page);
  await expectBranchDestinationsFollowPathOrder(page);
  await expectLabelsNearTheirArrows(page);
  await expectPathsAvoidUnrelatedBoxes(page);
  await expectCanvasContentUnclipped(page);
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    await expect(page.locator('.react-flow__node')).toHaveCount(4);
    await expectEdgeLabelsNotToOverlap(page);
    await expectBranchDestinationsFollowPathOrder(page);
    await expectLabelsNearTheirArrows(page);
    await expectPathsAvoidUnrelatedBoxes(page);
    await expectCanvasContentUnclipped(page);
  }
});

test('overview header wraps its actions below a one-line title at 1280 by 800', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, typedResultsDefinition());
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  const title = page.getByRole('heading', { name: 'Invoice approval', level: 1 }).last();
  await expect(page.locator('.workflow-canvas-surface')).toBeVisible();
  const titleBounds = await title.boundingBox();
  const canvasBounds = await page.locator('.workflow-canvas-surface').boundingBox();
  expect(titleBounds).not.toBeNull();
  expect(canvasBounds).not.toBeNull();
  expect(titleBounds!.height).toBeLessThanOrEqual(40);
  expect(canvasBounds!.y).toBeLessThan(260);
  const stepBox = await page.locator('.workflow-canvas-box').first().boundingBox();
  expect(stepBox).not.toBeNull();
  expect(stepBox!.width).toBeGreaterThan(160);
});

test('editor canvas fits below the header and keeps the invoice readable', async ({ page }) => {
  const definition = typedResultsDefinition();
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    await openEditor(page, definition);
    const canvas = page.locator('.workflow-canvas-surface');
    await expect(canvas).toBeVisible();
    const bounds = await canvas.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height);
    const transform = await page.locator('.react-flow__viewport').getAttribute('style');
    expect(Number(transform?.match(/scale\(([^)]+)\)/)?.[1])).toBeGreaterThanOrEqual(0.65);
  }
});

test('selecting and editing a step keeps the canvas drawn and the view steady', async ({
  page,
}) => {
  await openEditor(page, plainInvoiceDefinition());
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    await page.waitForTimeout(150);
    const surface = page.locator('.workflow-canvas-surface');
    const viewportStyle = () => page.locator('.react-flow__viewport').getAttribute('style');
    const before = await viewportStyle();
    await selectStep(page, 'Step 1 · Accounts assistant');
    await expect(page.getByLabel('What should they do?')).toBeVisible();
    await expect(surface).toHaveAttribute('data-layout-ready', 'true');
    await page.waitForTimeout(150);
    expect(await viewportStyle()).toBe(before);
    await page
      .getByLabel('What should they do?')
      .fill('Check the invoice amount and prepare a clear summary.');
    await expect(surface).toHaveAttribute('data-layout-current', 'true');
    await expect(surface).toHaveAttribute('data-layout-ready', 'true');
    expect(await viewportStyle()).toBe(before);
    await expect(page.locator('.react-flow__node')).toHaveCount(4);
    await expect(page.getByRole('status')).toHaveCount(0);
    await expectCanvasFieldChipsInsideBoxes(page);
    await expectCanvasControlsClearOfBoxes(page);
    await expectCanvasTextReadable(page);
  }

  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
    { width: 390, height: 844 },
  ]) {
    await page.setViewportSize(viewport);
    await page.waitForTimeout(200);
    await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
      'data-layout-ready',
      'true'
    );
    await expectNodesFitOrCanPan(page);
    await expectCanvasFieldChipsInsideBoxes(page);
    await expectCanvasControlsClearOfBoxes(page);
    await expectCanvasContentUnclipped(page);
    if (viewport.width >= 1024) await expectCanvasTextReadable(page);
  }
});

test('edge labels and add buttons stay clear of boxes when a check skips a review and joins', async ({
  page,
}) => {
  await openEditor(page, checkSkipsReviewAndJoinsDefinition());
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    const labels = page.locator('.workflow-canvas-edge-label');
    await expect(labels).toHaveCount(3);
    await expect(labels.locator('button')).toHaveCount(3);
    await page.mouse.move(5, 5);
    const firstLabel = labels.first();
    const addControl = firstLabel.locator('.workflow-canvas-add-action');
    await expect(addControl).toHaveCSS('opacity', '0');
    await expectEdgeLabelsNotToOverlap(page);
    await firstLabel.hover();
    await expect(addControl).toHaveCSS('opacity', '1');
    await expectEdgeLabelsNotToOverlap(page);
    await expectBranchDestinationsFollowPathOrder(page);
    await expectLabelsNearTheirArrows(page);
    await expectPathsAvoidUnrelatedBoxes(page);
    await expectCanvasContentUnclipped(page);
    await page.goto('/workflows');
    await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
    await expectEdgeLabelsNotToOverlap(page);
    await expectBranchDestinationsFollowPathOrder(page);
    await expectLabelsNearTheirArrows(page);
    await expectPathsAvoidUnrelatedBoxes(page);
    await page.getByRole('button', { name: 'Edit workflow' }).click();
  }
});

test('layered branches keep their boxes and connector paths clear', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, layeredBranchingDefinition());

  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    await expect(page.locator('.react-flow__node')).toHaveCount(8);
    await expectEdgeLabelsNotToOverlap(page);
    await expectPathsAvoidUnrelatedBoxes(page);
    await expectBranchDestinationsFollowPathOrder(page);
    await expectStartCenteredAndSiblingPathsSeparated(page);
  }

  await page.goto('/workflows');
  await page.getByRole('button', { name: /Layered branches 7 steps/ }).click();
  await expect(page.locator('.react-flow__node')).toHaveCount(8);
  await expectEdgeLabelsNotToOverlap(page);
  await expectPathsAvoidUnrelatedBoxes(page);
});

test('a short branch stays beside its siblings when it joins a later step', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, shortBranchJoiningLongerPathsDefinition());

  const branchTops = await page.evaluate(() =>
    ['short', 'middle', 'long'].map((key) => {
      const rect = document
        .querySelector<HTMLElement>(`.react-flow__node[data-id="${key}"]`)
        ?.getBoundingClientRect();
      return rect?.top ?? Number.NaN;
    })
  );
  expect(branchTops.every(Number.isFinite)).toBe(true);
  expect(Math.max(...branchTops) - Math.min(...branchTops)).toBeLessThanOrEqual(2);
  await expectPathsAvoidUnrelatedBoxes(page);
  await expectEdgeLabelsNotToOverlap(page);
  await expectBranchDestinationsFollowPathOrder(page);
});

test('an If and Otherwise split keeps its two branches balanced around the check', async ({
  page,
}) => {
  await openEditor(page, statusCheckDefinition());
  const centers = await page.evaluate(() => {
    const center = (key: string) => {
      const bounds = document
        .querySelector<HTMLElement>(`.react-flow__node[data-id="${key}"]`)
        ?.getBoundingClientRect();
      return bounds
        ? { x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 }
        : null;
    };
    return {
      check: center('accounts'),
      ifBranch: center('manager'),
      otherwiseBranch: center('approver'),
    };
  });
  expect(centers.check).not.toBeNull();
  expect(centers.ifBranch).not.toBeNull();
  expect(centers.otherwiseBranch).not.toBeNull();
  expect(centers.ifBranch!.x).toBeLessThan(centers.otherwiseBranch!.x);
  expect(
    Math.abs((centers.ifBranch!.x + centers.otherwiseBranch!.x) / 2 - centers.check!.x)
  ).toBeLessThanOrEqual(32);
});

test('straight sequential workflows use arrows to show which step comes next', async ({ page }) => {
  await openEditor(page, fiveStepChainDefinition());
  const paths = page.locator('.react-flow__edge-path');
  await expect(paths).toHaveCount(5);
  for (const path of await paths.all()) {
    await expect(path).toHaveAttribute('marker-end', /arrowclosed/i);
  }
  const startArrow = await page.evaluate(() => {
    const edge = Array.from(document.querySelectorAll<HTMLElement>('.react-flow__edge')).find(
      (candidate) => candidate.dataset.id?.startsWith('__workflow_start__-')
    );
    const path = edge?.querySelector<SVGPathElement>('.react-flow__edge-path');
    const targetId = edge?.dataset.id?.slice('__workflow_start__-'.length);
    const target = targetId
      ? document.querySelector<HTMLElement>(`.react-flow__node[data-id="${CSS.escape(targetId)}"]`)
      : null;
    if (!path?.getScreenCTM() || !target) return null;
    const endpoint = new DOMPoint(
      path.getPointAtLength(path.getTotalLength()).x,
      path.getPointAtLength(path.getTotalLength()).y
    ).matrixTransform(path.getScreenCTM()!);
    return {
      endpointY: endpoint.y,
      targetTop: target.getBoundingClientRect().top,
      markerEnd: path.getAttribute('marker-end'),
      handles: Array.from(target.querySelectorAll<HTMLElement>('.react-flow__handle')).map(
        (handle) => getComputedStyle(handle).opacity
      ),
    };
  });
  expect(startArrow).not.toBeNull();
  expect(startArrow!.markerEnd).toMatch(/arrowclosed/i);
  expect(Math.abs(startArrow!.endpointY - startArrow!.targetTop)).toBeLessThanOrEqual(2);
  expect(startArrow!.handles.every((opacity) => opacity === '0')).toBe(true);
});

test('Mortgage template edge labels stay by their arrows in the editor and overview', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openEditor(page, mortgageTemplateDefinition());
  await expect(page.locator('.workflow-canvas-edge-label')).toHaveCount(3);
  await expectEdgeLabelsNotToOverlap(page);
  await expectBranchDestinationsFollowPathOrder(page);
  await expectStartCenteredAndSiblingPathsSeparated(page);
  await expectLabelsNearTheirArrows(page);
  await expectPathsAvoidUnrelatedBoxes(page);

  await page.goto('/workflows');
  await page.getByRole('button', { name: /Mortgage 4 steps/ }).click();
  await expect(page.locator('.react-flow__node')).toHaveCount(5);
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    await expect(page.locator('.react-flow__node')).toHaveCount(5);
    await expectEdgeLabelsNotToOverlap(page);
    await expectLabelsNearTheirArrows(page);
    await expectBranchDestinationsFollowPathOrder(page);
    await expectStartCenteredAndSiblingPathsSeparated(page);
    await expectPathsAvoidUnrelatedBoxes(page);
  }
});

test('two checks and Otherwise labels stay apart in the editor and overview at desktop sizes', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, twoChecksOtherwiseDefinition('Two checks'));
  await expect(page.locator('.workflow-canvas-edge-label')).toHaveCount(3);
  await expectEdgeLabelsNotToOverlap(page);
  await expectBranchDestinationsFollowPathOrder(page);
  await expectPathsAvoidUnrelatedBoxes(page);

  await page.goto('/workflows');
  await page.getByRole('button', { name: /Two checks 4 steps/ }).click();
  await expect(page.locator('.workflow-canvas-edge-label')).toHaveCount(3);
  await expectEdgeLabelsNotToOverlap(page);
  await expectPathsAvoidUnrelatedBoxes(page);

  await page.getByRole('button', { name: 'Edit workflow' }).click();
  await page.setViewportSize({ width: 1440, height: 900 });
  await expectEdgeLabelsNotToOverlap(page);
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Two checks 4 steps/ }).click();
  await expectEdgeLabelsNotToOverlap(page);
});

test('six ordered branches and their shared ending fit without crossing boxes', async ({
  page,
}) => {
  await openEditor(page, sixBranchDefinition());
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
  ]) {
    await page.setViewportSize(viewport);
    await expectNodesFitOrCanPan(page);
    await expectCanvasWithinViewport(page);
    await expectCanvasContentUnclipped(page);
    await expectCanvasTextReadable(page);
    await expectPathsAvoidUnrelatedBoxes(page);
    await expectBranchDestinationsFollowPathOrder(page);
    await expectEdgeLabelsNotToOverlap(page);
    await page.goto('/workflows');
    await page.getByRole('button', { name: /Six branch workflow 8 steps/ }).click();
    await expectNodesFitOrCanPan(page);
    await expectCanvasWithinViewport(page);
    await expectCanvasContentUnclipped(page);
    await expectCanvasTextReadable(page);
    await expectPathsAvoidUnrelatedBoxes(page);
    await expectBranchDestinationsFollowPathOrder(page);
    await expectEdgeLabelsNotToOverlap(page);
    await page.getByRole('button', { name: 'Edit workflow' }).click();
  }
});

test('a nested check can join an earlier branch without crossings or detached arrows', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, nestedBranchJoinDefinition());

  await expect(page.locator('.react-flow__node')).toHaveCount(7);
  const addAction = page
    .locator('.react-flow__node[data-id="second"] .workflow-canvas-add-terminal')
    .getByRole('button', { name: 'What happens next?' });
  await panNodeIntoView(page, 'second');
  await addAction.click();
  await page.getByRole('button', { name: 'Continue with an existing step' }).click();
  await page.getByRole('option', { name: /Step 4/ }).click();

  await expect(page.locator('.react-flow__node')).toHaveCount(7);
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  await expectNodesFitOrCanPan(page);
  await expectCanvasWithinViewport(page);
  await expectCanvasTextReadable(page);
  await expectEdgeLabelsNotToOverlap(page);
  await expectLabelsNearTheirArrows(page);
  await expectPathsAvoidUnrelatedBoxes(page);
  await expectCanvasContentUnclipped(page);
  await expectEdgeArrowsToMeetTheirTargetBoxes(page);
  const unroundedCorners = await page.locator('.react-flow__edge path').evaluateAll(
    (paths) =>
      paths.filter((path) => {
        const edgeId = path.closest<HTMLElement>('.react-flow__edge')?.dataset.id ?? '';
        const pathData = path.getAttribute('d') ?? '';
        return (
          !edgeId.startsWith('__workflow_start__-') &&
          (pathData.match(/ L /g)?.length ?? 0) > 1 &&
          !pathData.includes(' Q ')
        );
      }).length
  );
  expect(unroundedCorners).toBe(0);
  const pathsOutsideCanvas = await page.evaluate(() => {
    const surface = document.querySelector<HTMLElement>('.workflow-canvas-surface');
    if (!surface) return ['canvas missing'];
    const bounds = surface.getBoundingClientRect();
    return Array.from(document.querySelectorAll<SVGPathElement>('.react-flow__edge path')).flatMap(
      (path) => {
        const matrix = path.getScreenCTM();
        if (!matrix) return ['edge path has no screen transform'];
        const length = path.getTotalLength();
        const outside = Array.from({ length: 33 }, (_, index) => {
          const point = path.getPointAtLength((length * index) / 32).matrixTransform(matrix);
          return (
            point.x < bounds.left - 2 ||
            point.y < bounds.top - 2 ||
            point.x > bounds.right + 2 ||
            point.y > bounds.bottom + 2
          );
        }).some(Boolean);
        return outside
          ? [`${path.closest<HTMLElement>('.react-flow__edge')?.dataset.id} leaves the canvas`]
          : [];
      }
    );
  });
  expect(pathsOutsideCanvas).toEqual([]);
});

test('selecting a step does not change its layout coordinates', async ({ page }) => {
  await openEditor(page, plainInvoiceDefinition());
  await panStepIntoView(page, 'Step 1 · Accounts assistant');
  const step = page.locator('.react-flow__node[data-id="accounts"]');
  const before = await step.getAttribute('style');
  await selectStep(page, 'Step 1 · Accounts assistant');
  await expect(page.getByRole('heading', { name: 'Step 1 · Accounts assistant' })).toBeVisible();
  await expect(step).toHaveAttribute('style', before!);
  const startVisible = await page.evaluate(() => {
    const start = document
      .querySelector<HTMLElement>('.workflow-canvas-surface')
      ?.getBoundingClientRect();
    const node = document
      .querySelector<HTMLElement>('.react-flow__node[data-id="__workflow_start__"]')
      ?.getBoundingClientRect();
    return Boolean(
      start &&
      node &&
      node.left >= start.left &&
      node.top >= start.top &&
      node.right <= start.right &&
      node.bottom <= start.bottom
    );
  });
  expect(startVisible).toBe(true);
});

test('overview hides Edit workflow on phones', async ({ page }) => {
  await openEditor(page, typedResultsDefinition());
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeHidden();
  await expect(page.getByRole('button', { name: 'Autopilot' })).toBeVisible();
});

test('a phone shows only the edit notice for an empty workflow start panel', async ({ page }) => {
  await openEditor(page, blankDefinition());
  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload();
  await expect(page.getByText('Edit this workflow on a larger screen.')).toBeVisible();
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(page.getByText('Edit this workflow on a larger screen.')).toBeVisible();
  await expect(page.getByLabel('Who does the first step?')).toHaveCount(0);
});

test('overview step selection shows a plain read-only summary without disabled editor controls', async ({
  page,
}) => {
  await openEditor(page, typedResultsDefinition());
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  await selectStep(page, 'Step 1 · Accounts assistant');
  const summary = page.getByRole('region', { name: 'Step summary' });
  await expect(summary).toBeVisible();
  await expect(summary).toContainText('Accounts assistant');
  await expect(summary).toContainText('Read the invoice.');
  await expect(summary).toContainText('Amount');
  await expect(summary).toContainText('If Amount is more than 500, then Step 2 · Manager');
  await expect(page.getByRole('button', { name: 'Delete step' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add a result' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add another check' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Test this step' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'What happens next?' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toHaveCount(1);
  await expect(page.locator('.workflow-canvas-actions')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Close', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  const start = await page.getByRole('button', { name: 'Start', exact: true }).boundingBox();
  expect(start).not.toBeNull();
  expect(start!.y).toBeGreaterThanOrEqual(0);
  expect(start!.y + start!.height).toBeLessThanOrEqual(900);
});

test('removed-agent overview summary never suggests choosing another agent', async ({ page }) => {
  const definition = typedResultsDefinition();
  definition.nodes[2] = { ...definition.nodes[2], agentId: 'removed-agent-id' };
  await openEditor(page, definition);
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  await selectStep(page, 'Step 3 · No one assigned');
  const summary = page.getByRole('region', { name: 'Step summary' });
  await expect(summary.getByRole('heading', { level: 2 })).toHaveText('Step 3 · No one assigned');
  await expect(page.getByRole('button', { name: 'Step 3 · No one assigned' })).toContainText(
    'No one assigned'
  );
  await expect(
    summary.getByText('The agent for this step was removed. Use Edit workflow to choose another.')
  ).toBeVisible();
  await expect(summary).not.toContainText('Choose another.');
  const labels = await page
    .locator('[aria-label], [title]')
    .evaluateAll((elements) =>
      elements.map(
        (element) =>
          `${element.getAttribute('aria-label') ?? ''} ${element.getAttribute('title') ?? ''}`
      )
    );
  expect(labels.join(' ')).not.toContain('Choose another');
});

test('removed agents show a warning on the step and the explanation in its settings', async ({
  page,
}) => {
  const definition = typedResultsDefinition();
  definition.nodes[2] = { ...definition.nodes[2], agentId: 'removed-agent-id' };
  await openEditor(page, definition);

  const step = page.getByRole('button', { name: 'Step 3 · Removed agent' });
  const warning = page.getByRole('img', {
    name: 'Step 3 (Removed agent) needs a different agent.',
  });
  await expect(warning).toBeVisible();
  await expect(step).not.toContainText('Step 3 (Removed agent) needs a different agent.');

  await selectStep(page, 'Step 3 · Removed agent');
  await expect(
    page
      .locator('.workflow-canvas-settings')
      .getByText('Step 3 (Removed agent) needs a different agent.', { exact: true })
  ).toBeVisible();
});

test('Start and Step 1 open in view without a later zoom change', async ({ page }) => {
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 1440, height: 900 },
    { width: 390, height: 844 },
  ]) {
    await page.setViewportSize(viewport);
    await openEditor(page, sixBranchDefinition());
    await expectStartAndFirstStepInView(page, 'branch');

    await page.goto('/workflows');
    await page.getByRole('button', { name: /Six branch workflow 8 steps/ }).click();
    await expectStartAndFirstStepInView(page, 'branch');

    if (viewport.width >= 1024) {
      await page.getByRole('button', { name: 'Edit workflow' }).click();
      await expectStartAndFirstStepInView(page, 'branch');
    }
  }
});

test('step boxes keep the agent name, opening instruction and every result name readable', async ({
  page,
}) => {
  await openEditor(page, typedResultsDefinition());
  const accountStep = page.locator('.react-flow__node[data-id="accounts"]');
  await expect(accountStep).toContainText('Accounts assistant');
  await expect(accountStep).toContainText('Read the invoice.');
  await expect(accountStep).toContainText('Amount');
  await expect(accountStep).toContainText('Approved');
  await expect(accountStep).toContainText('Category');
  await expectCanvasContentUnclipped(page);
  await expectCanvasFieldChipsInsideBoxes(page);
});

test('overview canvas has no focusable nodes or edges', async ({ page }) => {
  await openEditor(page, typedResultsDefinition());
  await page.goto('/workflows');
  await page.getByRole('button', { name: /Invoice approval 3 steps/ }).click();
  const focusableNames = await page
    .locator('button, a, input, select, textarea, [tabindex]')
    .evaluateAll((elements) =>
      elements
        .filter((element) => (element as HTMLElement).tabIndex >= 0)
        .map((element) => {
          const item = element as HTMLElement;
          return [item.getAttribute('aria-label'), item.getAttribute('title'), item.innerText]
            .filter(Boolean)
            .join(' ');
        })
    );
  expect(focusableNames.some((name) => name.includes('__workflow_start__'))).toBe(false);
  expect(focusableNames.some((name) => name.includes('Edge from'))).toBe(false);
});

test('a workflow with no start fields saves and runs without start-field hints', async ({
  page,
}) => {
  const editor = await openEditor(page, noStartFieldsDefinition());
  await expect(page.getByText('No start fields')).toBeVisible();
  await expect(page.getByText('Fill in the start fields when you run it.')).toHaveCount(0);
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(
    page.getByText('Optional. Add fields if each run needs details from you.')
  ).toBeVisible();
  await page.getByRole('button', { name: 'Close settings' }).click();
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Saved.')).toBeVisible();
  await page.getByRole('button', { name: 'Run workflow' }).last().click();
  await expect(page.getByText('This workflow needs nothing to start.')).toBeVisible();
  await page.getByRole('button', { name: 'Run workflow' }).last().click();
  await expect.poll(() => editor.getStartRequest()).not.toBeNull();
  expect(editor.getStartRequest()?.input).toEqual({});
  await expect(page.getByText('Enter the starting information for this run.')).toHaveCount(0);
});

test('first save keeps the selected new step panel open and one Back returns to the list', async ({
  page,
}) => {
  let created: Record<string, unknown> | null = null;
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path.endsWith('/auth/setup/status')) return route.fulfill({ json: { needsSetup: false } });
    if (path.endsWith('/auth/refresh'))
      return route.fulfill({ json: { accessToken: 'e2e-token', expiresInSeconds: 3600 } });
    if (path.endsWith('/auth/me'))
      return route.fulfill({ json: { id: 'owner', role: 'Admin', userName: 'Owner' } });
    if (path.endsWith('/settings/agents')) return route.fulfill({ json: agents });
    if (path.endsWith('/workflows') && url.searchParams.get('archived') === 'true')
      return route.fulfill({ json: [] });
    if (path.endsWith('/workflows') && route.request().method() === 'POST') {
      const request = route.request().postDataJSON() as Record<string, unknown>;
      const now = new Date().toISOString();
      created = {
        ...request,
        id: 'wf-created',
        ownerUserId: 'owner',
        revision: 1,
        createdAt: now,
        updatedAt: now,
        diagram: null,
      };
      return route.fulfill({ json: created });
    }
    if (path.endsWith('/workflows') && route.request().method() === 'GET')
      return route.fulfill({ json: created ? [created] : [] });
    if (path.endsWith('/workflows/wf-created')) return route.fulfill({ json: created });
    return route.fulfill({ json: {} });
  });
  await page.goto('/workflows');
  await page.getByRole('button', { name: 'New workflow' }).first().click();
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByLabel('Who does the first step?').selectOption('reviewer');
  await page.getByRole('button', { name: 'Add first step' }).click();
  await page.getByRole('button', { name: /Step 1 · Accounts assistant/ }).click();
  await page.getByLabel('What should they do?').fill('Check my inbox.');
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page).toHaveURL(/\/workflows\/wf-created\/edit$/);
  await expect(page.getByRole('button', { name: 'Delete step' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save workflow' })).toBeFocused();
  await page.goBack();
  await expect(page).toHaveURL(/\/workflows$/);
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
});

test('adds a typed result and saves its label and key when Enter submits the focused field', async ({
  page,
}) => {
  const editor = await openEditor(page, typedResultsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByRole('button', { name: 'Add a result' }).click();
  const resultName = page.getByLabel('Results to pass on field name').last();
  await resultName.pressSequentially('Summary');
  await resultName.press('Enter');
  await expect
    .poll(() => editor.getDefinition().nodes[0]?.outputSchema.properties.summary)
    .toMatchObject({ title: 'Summary' });
});

test('saves a new result with its key while the field still has focus', async ({ page }) => {
  const editor = await openEditor(page, typedResultsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByRole('button', { name: 'Add a result' }).click();
  const field = page.getByLabel('Results to pass on field name').last();
  await field.pressSequentially('Summary');
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect
    .poll(() => editor.getDefinition().nodes[0]?.outputSchema.properties.summary)
    .toMatchObject({ title: 'Summary' });
});

test('pick-list fields show answers and block saving until an answer exists', async ({ page }) => {
  await openEditor(page);
  await addFirstStep(page);
  await setAgentAndPrompt(page, 'Step 1 · Accounts assistant', 'reviewer', 'Read the invoice.');
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByRole('button', { name: 'Add a start field' }).click();
  await page.getByLabel('New start field name').fill('Category');
  await page.getByLabel('New start field type').selectOption('pick-list');
  await page.getByRole('button', { name: 'Add input' }).click();
  await expect(page.getByText('Add at least one answer.', { exact: true })).toBeVisible();
  await page.getByLabel('New start field answers').fill('Invoice, Refund');
  await page.getByRole('button', { name: 'Add input' }).click();
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('Results to pass on field name').fill('Decision');
  await page.getByLabel('Results to pass on field type').selectOption('pick-list');
  await expect(page.getByLabel('Results to pass on answers')).toBeVisible();
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Add at least one answer', { exact: true }).first()).toBeVisible();
});

test('Step 1 adds a start field on Enter and requires answers for a pick-list', async ({
  page,
}) => {
  await openEditor(page);
  await addFirstStep(page);
  await setAgentAndPrompt(page, 'Step 1 · Accounts assistant', 'reviewer', 'Read the invoice.');
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByRole('button', { name: 'Add a start field' }).click();
  await page.getByLabel('New start field name').fill('Category');
  await page.getByLabel('New start field type').selectOption('pick-list');
  await page.getByLabel('New start field answers').fill('Invoice, Refund');
  await page.getByLabel('New start field name').press('Enter');
  await expect(page.getByRole('button', { name: 'Category' })).toBeVisible();
  await page.getByRole('button', { name: 'Add a start field' }).click();
  await page.getByLabel('New start field name').fill('Status');
  await page.getByLabel('New start field type').selectOption('pick-list');
  await page.getByRole('button', { name: 'Add input' }).click();
  await expect(page.getByText('Add at least one answer.', { exact: true })).toBeVisible();
});

async function startChoice(page: Page) {
  await panStepIntoView(page, 'Step 1 · Accounts assistant');
  await expect(
    page.locator('.workflow-canvas-add-terminal').getByRole('button', {
      name: 'What happens next?',
    })
  ).toBeVisible();
  await page.getByRole('button', { name: 'What happens next?', exact: true }).click();
  await page.getByRole('button', { name: 'Check something', exact: true }).click();
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
}

async function selectStep(page: Page, label: string) {
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
  const step = page.locator(`.workflow-canvas-box-open[aria-label="${label}"]`);
  const node = step.locator('xpath=../../..');
  const nodeId = await node.getAttribute('data-id');
  expect(nodeId).toBeTruthy();
  await panNodeIntoView(page, nodeId!);
  await step.click();
}

async function panStepIntoView(page: Page, label: string) {
  const step = page.locator(`.workflow-canvas-box-open[aria-label="${label}"]`);
  const nodeId = await step.locator('xpath=../../..').getAttribute('data-id');
  expect(nodeId).toBeTruthy();
  await panNodeIntoView(page, nodeId!);
  return nodeId!;
}

async function setAgentAndPrompt(page: Page, step: string, agentId: string, prompt: string) {
  await selectStep(page, step);
  await page.getByLabel('Who does this?').selectOption(agentId);
  await page.getByLabel('What should they do?').fill(prompt);
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-current',
    'true'
  );
  await page.getByRole('button', { name: 'Close settings' }).click();
  await expect(page.locator('.workflow-canvas-surface')).toHaveAttribute(
    'data-layout-ready',
    'true'
  );
}

test('tests a step with its available values and shows the results without saving', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, typedResultsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByRole('button', { name: 'Test this step' }).click();
  await expect(page.getByRole('dialog', { name: 'Test this step' })).toBeVisible();
  await page.getByRole('textbox', { name: 'Invoice' }).fill('INV-12');
  await page.getByRole('button', { name: 'Test step' }).click();
  await expect(page.getByLabel('Step test results')).toContainText('Amount: 520');
  await expect(page.getByLabel('Step test results')).toContainText('Approved: Yes');
});

test('selects a step when the lower half of its box is clicked', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, typedResultsDefinition());
  await page.getByRole('button', { name: 'Start', exact: true }).click();

  const box = page.locator('.react-flow__node[data-id="accounts"]');
  // Selecting Start pans the canvas. Wait for that transform to settle before
  // measuring the step, or the click point can refer to its pre-pan position.
  await page.waitForFunction(async () => {
    const node = document.querySelector('.react-flow__node[data-id="accounts"]');
    if (!node) return false;
    const readBounds = () => {
      const { x, y, width, height } = node.getBoundingClientRect();
      return [x, y, width, height];
    };
    let previous = readBounds();
    let stableFrames = 0;
    while (stableFrames < 3) {
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      const current = readBounds();
      const stable = current.every((value, index) => Math.abs(value - previous[index]!) < 0.5);
      stableFrames = stable ? stableFrames + 1 : 0;
      previous = current;
    }
    return true;
  });
  const bounds = await box.boundingBox();
  expect(bounds).not.toBeNull();
  await box.click({ position: { x: 20, y: bounds!.height * 0.8 } });

  await expect(page.getByRole('heading', { name: 'Step 1 · Accounts assistant' })).toBeVisible();
  await expect(page.getByLabel('What should they do?')).toBeVisible();
});

test('governed steps without results are named and selected before the Gateway save', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, governedMissingResultsDefinition());
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByRole('alert')).toContainText(
    'Step 2 (Accounts assistant) needs at least one result to pass on.'
  );
  await expect(page.getByLabel('What should they do?')).toBeVisible();
  await expect(page.getByText('Request failed with status code 400')).toHaveCount(0);
});

test('clears the save error as soon as the draft changes', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByRole('alert')).toHaveText('Add at least one step.');
  await addFirstStep(page);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('Autopilot and browser Back ask before leaving an edited workflow', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/dashboard');
  await openEditor(page, typedResultsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('What should they do?').fill('Check the total before continuing.');
  await expect(page.getByText('Type {{name}} in an instruction to use a value.')).toHaveCount(0);
  page.once('dialog', async (dialog) => {
    expect(dialog.message()).toBe('Leave without saving?');
    await dialog.dismiss();
  });
  await page.getByRole('button', { name: 'Autopilot' }).click();
  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e\/edit$/);
  await page.evaluate(() =>
    window.history.pushState(
      { ...(window.history.state ?? {}), testHistoryEntry: true },
      '',
      `${window.location.pathname}?history-test`
    )
  );
  page.once('dialog', async (dialog) => {
    expect(dialog.message()).toBe('Leave without saving?');
    await dialog.dismiss();
  });
  await page.goBack({ waitUntil: 'commit' });
  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e\/edit$/);
  await expect(page.getByLabel('What should they do?')).toHaveValue(
    'Check the total before continuing.'
  );
});

test('edits each If from the current step results using its own result kind', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const { getDefinition } = await openEditor(page, typedResultsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');

  const firstResult = page.getByLabel('If 1 result');
  await expect(firstResult.locator('option')).toContainText([
    'Invoice',
    'Amount',
    'Approved',
    'Category',
  ]);
  await expect(firstResult.locator('option').last()).toHaveText(
    'Add a result to Step 1 · Accounts assistant'
  );
  await firstResult.selectOption('amount');
  await page.getByLabel('If 1 comparison').selectOption('gt');
  await expect(page.getByLabel('If 1 comparison').locator('option')).toHaveText([
    'is more than',
    'is less than',
    'is',
    'is at least',
    'is at most',
    'has a value',
  ]);
  const numberValue = page.getByLabel('If 1 value');
  await expect(numberValue).toHaveAttribute('type', 'number');
  await numberValue.fill('500.0');
  await numberValue.blur();
  await expect(
    page
      .locator('[aria-label="Choice paths"]')
      .getByText('If Amount is more than 500', { exact: true })
  ).toBeVisible();

  await page.getByLabel('If 2 result').selectOption('approved');
  const yesNo = page.getByLabel('If 2 comparison');
  await expect(yesNo.locator('option')).toHaveText(['is Yes', 'is No', 'has a value']);
  await yesNo.selectOption({ label: 'is No' });
  await expect(page.getByLabel('If 2 answer')).toHaveCount(0);
  await expect(
    page.locator('[aria-label="Choice paths"]').getByText('If Approved is No', { exact: true })
  ).toBeVisible();

  await page.getByLabel('If 3 result').selectOption('category');
  const pickList = page.getByRole('combobox', { name: 'If 3 answer' });
  await expect(pickList).toHaveAttribute('role', 'combobox');
  await pickList.fill('Compla');
  await expect(
    page.getByRole('listbox', { name: 'If 3 answer options' }).getByRole('option')
  ).toHaveText(['Complaint', 'Add ‘Compla’ as an answer']);
  await pickList.fill('Complaint');
  await expect(
    page.getByRole('listbox', { name: 'If 3 answer options' }).getByRole('option')
  ).toHaveText(['Complaint']);
  await page.getByRole('option', { name: 'Complaint', exact: true }).click();
  await expect(page.getByLabel('If 3 value')).toHaveCount(0);
  await expect(
    page
      .locator('[aria-label="Choice paths"]')
      .getByText('If Category is Complaint', { exact: true })
  ).toBeVisible();
  const resultNamesBeforeAdd = await page.getByLabel('Results to pass on field name').count();
  await page.getByLabel('If 3 result').selectOption('__add_result__');
  await expect(page.getByRole('heading', { name: 'Results to pass on' })).toBeVisible();
  const resultNames = page.getByLabel('Results to pass on field name');
  await expect(resultNames).toHaveCount(resultNamesBeforeAdd + 1);
  await expect(resultNames.last()).toBeFocused();
  await expect(resultNames.last()).toHaveValue('');
  await resultNames.last().fill('reviewNote');

  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Saved.')).toBeVisible();
  const cases = getDefinition().nodes[0]?.switchCases as Array<{
    op: string;
    value: string | null;
  }>;
  expect(cases[0]).toMatchObject({ op: 'gt', value: '500' });
  expect(cases[1]).toMatchObject({ op: 'eq', value: 'false' });
  expect(cases[2]).toMatchObject({ op: 'eq', value: 'Complaint' });
});

test('the plus menu offers a step or a check and creates both paths', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await addFirstStep(page);
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('What should they do?').fill('Read the invoice and pull out the total.');
  await page.getByRole('button', { name: 'Close settings' }).click();
  await panStepIntoView(page, 'Step 1 · Accounts assistant');
  await page.getByRole('button', { name: 'What happens next?', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Add a step', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Check something', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Check something', exact: true }).click();
  await expect(page.locator('.workflow-canvas-box-open[aria-label^="Step 2"]')).toBeVisible();
  await expect(page.locator('.workflow-canvas-box-open[aria-label^="Step 3"]')).toBeVisible();
});

test('builds, saves, and reloads a three-step choice', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const { getDefinition } = await openEditor(page);
  await addFirstStep(page);
  await setAgentAndPrompt(
    page,
    'Step 1 · Accounts assistant',
    'reviewer',
    'Check the invoice amount.'
  );
  await startChoice(page);
  await page.getByRole('button', { name: 'Close settings' }).click();

  await selectStep(page, 'Step 2 · Choose who does this');
  await page.getByLabel('Who does this?').selectOption('manager');
  await page.getByLabel('What should they do?').fill('Review the high value invoice.');
  await page.getByRole('button', { name: 'Close settings' }).click();
  await selectStep(page, 'Step 3 · Choose who does this');
  await page.getByLabel('Who does this?').selectOption('approver');
  await page.getByLabel('What should they do?').fill('Approve the lower value invoice.');
  await page.getByRole('button', { name: 'Close settings' }).click();
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('If 1 result').selectOption('amount');
  await page.getByLabel('If 1 comparison').selectOption('gt');
  await page.getByLabel('If 1 value').fill('500');
  await page.getByRole('button', { name: 'Close settings' }).click();

  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Saved.')).toBeVisible();
  expect(getDefinition().nodes).toHaveLength(3);
  await page.reload();
  await expect(page.getByRole('button', { name: 'Step 2 · Manager', exact: true })).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Step 3 · Auto-approver', exact: true })
  ).toBeVisible();
  await selectStep(page, 'Step 1 · Accounts assistant');
  await expect(
    page.getByRole('region', { name: 'Choice paths' }).getByText('If Amount is more than 500', {
      exact: true,
    })
  ).toBeVisible();
});

test('saves and reloads the invoice diamond and keeps editing view-only on mobile', async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const { getDefinition } = await openEditor(page);
  await addFirstStep(page);
  await setAgentAndPrompt(
    page,
    'Step 1 · Accounts assistant',
    'reviewer',
    'Check the invoice and its amount.'
  );
  await startChoice(page);
  await page.getByRole('button', { name: 'Close settings' }).click();

  await setAgentAndPrompt(
    page,
    'Step 2 · Choose who does this',
    'manager',
    'Review and approve the high value invoice.'
  );
  await setAgentAndPrompt(
    page,
    'Step 3 · Choose who does this',
    'approver',
    'Approve the low value invoice.'
  );
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('If 1 result').selectOption('amount');
  await page.getByLabel('If 1 comparison').selectOption('gt');
  await page.getByLabel('If 1 value').fill('500');
  await page.getByRole('button', { name: 'Close settings' }).click();

  const managerNodeId = await panStepIntoView(page, 'Step 2 · Manager');
  await page
    .locator(`.react-flow__node[data-id="${managerNodeId}"] .workflow-canvas-add-terminal`)
    .getByRole('button', { name: 'What happens next?' })
    .click();
  await page.getByRole('button', { name: 'Add a step', exact: true }).click();
  await page.getByRole('button', { name: 'Filing clerk', exact: true }).click();
  await page.getByLabel('What should they do?').fill('File the invoice in Xero.');
  await page.getByRole('button', { name: 'Close settings' }).click();
  const fileStep = page.getByRole('button', { name: /Step \d+ · Filing clerk/ }).first();
  await expect(fileStep).toBeVisible();
  const fileStepName = await fileStep.getAttribute('aria-label');
  expect(fileStepName).toBeTruthy();

  const autoApproverNodeId = await panStepIntoView(page, 'Step 3 · Auto-approver');
  await page
    .locator(`.react-flow__node[data-id="${autoApproverNodeId}"] .workflow-canvas-add-terminal`)
    .getByRole('button', { name: 'What happens next?' })
    .click();
  await page.getByRole('button', { name: 'Continue with an existing step' }).click();
  await page
    .getByRole('listbox', { name: 'Continue with an existing step' })
    .getByRole('option', { name: fileStepName! })
    .click();
  await expect(page.getByText('Then', { exact: true })).toHaveCount(0);

  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Saved.')).toBeVisible();
  const saved = getDefinition() as {
    nodes: Array<{
      nodeId: string;
      agentId: string;
      switchCases?: Array<{ goTo: string }>;
      defaultGoTo?: string | null;
    }>;
  };
  const manager = saved.nodes.find((node) => node.agentId === 'manager');
  const approver = saved.nodes.find((node) => node.agentId === 'approver');
  const filer = saved.nodes.find((node) => node.agentId === 'filer');
  expect(manager?.defaultGoTo).toBe(filer?.nodeId);
  expect(approver?.defaultGoTo).toBe(filer?.nodeId);

  await page.reload();
  await expect(page.getByRole('button', { name: /Filing clerk/ })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Show workflows' })).toBeVisible();
  expect((await page.locator('.workflow-canvas-surface').boundingBox())?.width).toBeGreaterThan(
    850
  );
  const screenshotDir = testInfo.outputDir;
  await page.screenshot({ path: `${screenshotDir}/invoice-diamond-1280x800.png` });

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('link', { name: 'Back to workflows' })).toBeVisible();
  expect(
    await page
      .locator('.page-shell > div:first-child > div.flex')
      .evaluate((element) => getComputedStyle(element).flexWrap)
  ).toBe('wrap');
  await expect(page.getByText('Edit this workflow on a larger screen.')).toBeVisible();
  await expect(page.getByLabel('What should they do?')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Step 2 · Manager', exact: true })).toBeVisible();
  await page.screenshot({ path: `${screenshotDir}/invoice-diamond-mobile.png` });
});

test('undo and redo use the toolbar buttons and preserve saved state', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await addFirstStep(page);
  await selectStep(page, 'Step 1 · Accounts assistant');
  const prompt = page.getByLabel('What should they do?');
  await prompt.fill('Review the invoice.');
  await page.getByRole('button', { name: 'Close settings' }).click();
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await selectStep(page, 'Step 1 · Accounts assistant');
  await expect(prompt).toHaveValue('');
  await page.getByRole('button', { name: 'Redo', exact: true }).click();
  await expect(prompt).toHaveValue('Review the invoice.');
  await page.getByRole('button', { name: 'Close settings' }).click();
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Saved.')).toBeVisible();
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(page.getByText('Unsaved changes')).toBeVisible();
  await page.getByRole('button', { name: 'Redo', exact: true }).click();
  await selectStep(page, 'Step 1 · Accounts assistant');
  await expect(prompt).toHaveValue('Review the invoice.');
});

test('Add another check creates a blank step and the panel lists clickable destinations', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, typedResultsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');

  const checks = page.getByRole('region', { name: 'Choice paths' });
  await expect(checks.getByLabel('Agent for the next If path')).toHaveCount(0);
  await expect(page.getByText('Advanced', { exact: true })).toHaveCount(1);
  await expect(checks.getByRole('button', { name: 'Add another check' })).toBeVisible();
  await expect(checks.getByText(/Otherwise → Step 3 · Auto-approver/)).toBeVisible();
  await checks
    .getByRole('button', { name: /Step 2 · Manager/ })
    .last()
    .click();
  await expect(page.getByRole('heading', { name: 'Step 2 · Manager' })).toBeVisible();

  await page.getByRole('button', { name: 'Close settings' }).click();
  await selectStep(page, 'Step 1 · Accounts assistant');
  const before = await page.locator('.workflow-canvas-box').count();
  await checks.getByRole('button', { name: 'Add another check' }).click();
  await expect(page.locator('.workflow-canvas-box')).toHaveCount(before + 1);
  await expect(
    page.locator('button.workflow-canvas-box-open[aria-label="Step 3 · Choose who does this"]')
  ).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Step 1 · Accounts assistant' })).toBeVisible();
  await expect(page.getByLabel('If 4 result')).toBeVisible();
  await checks.getByLabel('If 4 result').selectOption('amount');
  await checks.getByLabel('If 4 comparison').selectOption('gt');
  await checks.getByLabel('If 4 value').fill('500');
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByRole('alert')).toContainText(
    'Step 3: choose who does this and add an instruction.'
  );
  await expect(page.getByRole('heading', { name: 'Step 3 · Choose who does this' })).toBeVisible();
  await expect(page.getByLabel('Who does this?')).toHaveValue('');
  const emptyStep = page.locator(
    '.workflow-canvas-box-open[aria-label="Step 3 · Choose who does this"]'
  );
  await expect(emptyStep).toContainText('Choose who does this');
  await expect(emptyStep).not.toContainText('Add an instruction');
});

test('field labels stay friendly after saving and reloading', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const { getDefinition } = await openEditor(page, typedResultsDefinition());
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByLabel('New start field name').fill('Customer name');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await page.getByRole('button', { name: 'Close settings' }).click();
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Saved.')).toBeVisible();
  expect(
    (getDefinition().inputSchema.properties as Record<string, { title?: string }>).customer_name
      ?.title
  ).toBe('Customer name');
  await page.reload();
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(page.getByLabel('Start fields field name').last()).toHaveValue('Customer name');
});

test('cleared result labels show one short named error', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, typedResultsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('Results to pass on field name').first().fill('');
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByRole('alert')).toHaveText(
    'Step 1 (Accounts assistant): add a name for this field.'
  );
  await expect(page.getByRole('alert')).toHaveCount(1);
});

test('renaming a result keeps its key, check, and next-step mention intact', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const { getDefinition } = await openEditor(page, statusCheckDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('Results to pass on field name').fill('Invoice status');
  await page.getByLabel('Results to pass on field name').blur();
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect(page.getByText('Saved.')).toBeVisible();

  const first = getDefinition().nodes[0]!;
  expect(first.outputSchema.properties).toMatchObject({ status: { title: 'Invoice status' } });
  expect(first.maps).toEqual([{ from: '/status', to: '/status' }]);
  expect(first.switchCases).toEqual([
    { pointer: '/status', op: 'eq', value: 'ready', goTo: 'manager' },
  ]);
  expect(getDefinition().nodes[1]!.promptTemplate).toContain('{{status}}');
});

test('Backspace on the plus menu does not delete a step and review is read only on boxes', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await addFirstStep(page);
  const count = await page.locator('.react-flow__node').count();
  const plus = page.getByRole('button', { name: 'What happens next?' });
  await plus.focus();
  await plus.press('Backspace');
  await expect(page.locator('.react-flow__node')).toHaveCount(count);
  await expect(page.locator('.workflow-canvas-box input[type="checkbox"]')).toHaveCount(0);
});

test('Back to workflows asks before discarding edits', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await addFirstStep(page);
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('What should they do?').fill('Review the request.');
  page.once('dialog', async (dialog) => {
    expect(dialog.message()).toBe('Leave without saving?');
    await dialog.accept();
  });
  await page.getByRole('link', { name: 'Back to workflows' }).click();
  await expect(page).toHaveURL(/\/workflows$/);
});

test('browser Back then Cancel keeps the workflow editor draft open', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await page.goto('/workflows');
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
  await page.locator('button[title="Invoice approval"]').click();
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeVisible();
  await page.getByRole('button', { name: 'Edit workflow' }).click();
  await expect(page.getByRole('button', { name: 'Show workflows' })).toBeVisible();
  await addFirstStep(page);
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('What should they do?').fill('Check this invoice carefully.');

  page.once('dialog', async (dialog) => {
    expect(dialog.message()).toBe('Leave without saving?');
    await dialog.dismiss();
  });
  await page.goBack({ waitUntil: 'commit' });

  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e\/edit$/);
  await expect(page.getByLabel('What should they do?')).toHaveValue(
    'Check this invoice carefully.'
  );
  await expect(page.getByText('Unsaved changes')).toBeVisible();
  await page.getByLabel('What should they do?').fill('Edit still works after cancelling.');
  await expect(page.getByLabel('What should they do?')).toHaveValue(
    'Edit still works after cancelling.'
  );
  page.once('dialog', async (dialog) => {
    expect(dialog.message()).toBe('Leave without saving?');
    await dialog.accept();
  });
  await page.goBack({ waitUntil: 'commit' });
  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e$/);
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeVisible();
});

test('settings overlay keeps the canvas width and closes on an empty canvas click', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByLabel('Who does the first step?').selectOption('reviewer');
  await page.getByRole('button', { name: 'Add first step' }).click();
  await page.getByRole('button', { name: 'Close settings' }).click();
  const surface = page.locator('.workflow-canvas-surface');
  const before = await surface.boundingBox();
  await selectStep(page, 'Step 1 · Accounts assistant');
  const after = await surface.boundingBox();
  expect(after?.width).toBe(before?.width);
  await page.locator('.react-flow__pane').click({ position: { x: 24, y: 420 } });
  await expect(page.locator('.workflow-canvas-settings')).toBeHidden();
});

test('Run workflow opens a dialog and sends the entered input and correlation key', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const { getStartRequest } = await openEditor(page, runFieldsDefinition());
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('What should they do?').fill('Read the invoice and return its details.');
  await page.getByRole('button', { name: 'Close settings' }).click();
  await page.getByRole('button', { name: 'Run workflow', exact: true }).first().click();
  const savePrompt = page.getByRole('dialog', { name: 'Save changes first?' });
  await expect(savePrompt).toBeVisible();
  await savePrompt.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByRole('button', { name: 'Run workflow', exact: true }).first().click();
  await page
    .getByRole('dialog', { name: 'Save changes first?' })
    .getByRole('button', { name: 'Save and run' })
    .click();
  const dialog = page.getByRole('dialog', { name: 'Run workflow' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Invoice').fill('INV-42');
  await dialog.getByText('More options').click();
  await dialog.getByLabel('Run ID (optional), stops the same job running twice').fill('case-100');
  await dialog.getByLabel('Category').selectOption('Refund');
  await dialog.getByLabel('Approved').selectOption('true');
  await expect(dialog.getByText(/Files come from Accounts assistant's workspace/)).toBeVisible();
  await dialog.locator('#workflow-run-form').getByRole('button', { name: 'Run workflow' }).click();
  await expect.poll(getStartRequest).toMatchObject({
    input: { invoice: 'INV-42', category: 'Refund', approved: true },
    correlationKey: 'case-100',
    files: [],
  });
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
  await page.getByRole('button', { name: 'Workflow actions for Invoice approval' }).click();
  await expect(page.getByRole('menuitem', { name: 'Archive workflow' })).toBeVisible();
});

test('Run dialog uploads a file and asks before replacing an existing file', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const { getUploadRequests, getStartRequest } = await openEditor(
    page,
    runFieldsDefinition(),
    'conflict-once'
  );
  await page.getByRole('button', { name: 'Run workflow', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Run workflow' });
  await expect(dialog).toBeVisible();
  page.once('dialog', async (browserDialog) => {
    expect(browserDialog.message()).toBe('Replace the file already there?');
    await browserDialog.accept();
  });
  await dialog.getByLabel('Upload a file').setInputFiles({
    name: 'invoice.pdf',
    mimeType: 'application/pdf',
    buffer: Buffer.from('invoice'),
  });
  await expect(dialog.getByText('invoice.pdf', { exact: true })).toBeVisible();
  await expect.poll(getUploadRequests).toHaveLength(2);
  expect(getUploadRequests()[0]).toMatch(/name="overwrite"\r\n\r\nfalse/);
  expect(getUploadRequests()[1]).toMatch(/name="overwrite"\r\n\r\ntrue/);
  await dialog.getByRole('textbox', { name: 'Invoice' }).fill('INV-42');
  await dialog.locator('#workflow-run-form').getByRole('button', { name: 'Run workflow' }).click();
  await expect.poll(getStartRequest).toMatchObject({ files: ['invoice.pdf'] });
});

test('Run dialog explains when file uploads are turned off for the first step agent', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page, runFieldsDefinition(), 'disabled');
  await page.getByRole('button', { name: 'Run workflow', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Run workflow' });
  await dialog.getByLabel('Upload a file').setInputFiles({
    name: 'invoice.pdf',
    mimeType: 'application/pdf',
    buffer: Buffer.from('invoice'),
  });
  await expect(dialog.getByText('Turn on files for Accounts assistant first.')).toBeVisible();
});

test('workflow review sends a change note, shows it on the retry, then approves on the run page', async ({
  page,
}) => {
  const definition = typedResultsDefinition();
  definition.nodes[0]!.requiresReview = true;
  definition.nodes[0]!.promptTemplate = 'Read the {{invoice}}.';
  const now = new Date().toISOString();
  const firstAttempt = {
    id: 'attempt-1',
    instanceId: 'run-1',
    nodeId: 'accounts',
    attempt: 1,
    runId: 'run-attempt-1',
    status: 'finished',
    createdAt: now,
    endedAt: now,
  };
  const retryAttempt = {
    ...firstAttempt,
    id: 'attempt-2',
    attempt: 2,
    runId: 'run-attempt-2',
    reviewNote: 'Please include the invoice date.',
  };
  const finalAttempt = {
    ...retryAttempt,
    id: 'attempt-3',
    attempt: 3,
    runId: 'run-attempt-3',
  };
  let instance = {
    id: 'run-1',
    workflowId: 'wf-canvas-e2e',
    definitionRevision: 1,
    ownerUserId: 'owner',
    status: 'paused',
    stopRequested: false,
    currentNodeId: 'accounts',
    envelope: { title: 'Invoice approval' },
    envelopeHash: 'hash',
    envelopeRevision: 1,
    snapshot: definition,
    steps: [firstAttempt],
    createdAt: now,
    updatedAt: now,
    startedAt: now,
  };
  const approval = (id: string, remainingChanges = 2) => ({
    id,
    toolCallId: `run-1:accounts:${id}`,
    toolName: 'workflow_review',
    toolType: 'workflow_review',
    requestedAt: now,
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    kind: 'workflow_review',
    workflowId: 'wf-canvas-e2e',
    instanceId: 'run-1',
    nodeId: 'accounts',
    remainingChangeRequests: remainingChanges,
    actions: [
      { label: 'Approve', value: 'approve', style: 'primary' },
      ...(remainingChanges > 0
        ? [
            {
              label: 'Request changes',
              value: 'request_changes',
              style: 'default',
            },
          ]
        : []),
      { label: 'Stop run', value: 'stop_run', style: 'danger', requiresRunId: true },
    ],
  });
  let pendingApprovals = [approval('approval-1')];
  const commands: Array<Record<string, unknown>> = [];

  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path.endsWith('/auth/setup/status')) return route.fulfill({ json: { needsSetup: false } });
    if (path.endsWith('/auth/refresh'))
      return route.fulfill({ json: { accessToken: 'e2e-token', expiresInSeconds: 3600 } });
    if (path.endsWith('/auth/me'))
      return route.fulfill({ json: { id: 'owner', role: 'Admin', userName: 'Owner' } });
    if (path.endsWith('/settings/agents')) return route.fulfill({ json: agents });
    if (path.endsWith('/runtime/execution-readiness'))
      return route.fulfill({
        json: { ready: true, code: 'READY', message: 'Ready', dependencies: [] },
      });
    if (path.endsWith('/approvals/pending')) return route.fulfill({ json: pendingApprovals });
    if (path.endsWith('/workflows/wf-canvas-e2e/instances/run-1/events'))
      return route.fulfill({ json: { events: [] } });
    if (path.endsWith('/workflows/wf-canvas-e2e/instances/run-1'))
      return route.fulfill({ json: instance });
    if (path.endsWith('/workflows/wf-canvas-e2e')) return route.fulfill({ json: definition });
    if (path.includes('/narad/v1/') && route.request().method() === 'POST') {
      const command = route.request().postDataJSON() as Record<string, unknown>;
      commands.push(command);
      if (command.decision === 'request_changes') {
        const attempts =
          instance.steps.length === 1
            ? [firstAttempt, retryAttempt]
            : [firstAttempt, retryAttempt, finalAttempt];
        instance = { ...instance, status: 'paused', steps: attempts };
        pendingApprovals =
          attempts.length < 3
            ? [approval(`approval-${attempts.length}`, 3 - attempts.length)]
            : [approval(`approval-${attempts.length}`, 0)];
      } else if (command.decision === 'approve') {
        instance = { ...instance, status: 'finished', currentNodeId: null };
        pendingApprovals = [];
      }
      return route.fulfill({ json: { status: 'accepted', decision: command.decision } });
    }
    return route.fulfill({ json: {} });
  });

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/workflows/wf-canvas-e2e/instances/run-1');
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeHidden();
  await expect(page.getByText('Step 1 is ready for you to check')).toBeVisible();
  await expect(page.getByText('2 more change requests left.')).toBeVisible();
  await expect(page.getByText('continue or stop from Approvals')).toHaveCount(0);
  const detailSummaries = page.getByText('Details', { exact: true });
  await detailSummaries.last().click();
  await expect(page.getByText('All run values')).toBeVisible();
  if ((await detailSummaries.count()) > 1) {
    await detailSummaries.first().click();
    await expect(page.getByText('Instructions').first()).toBeVisible();
  }
  const runText = await page.locator('body').innerText();
  expect(runText).not.toMatch(
    /Canonical|envelope|contract|\brev\s|hash|payload|schema|\bnode\b|trace|\{\{/i
  );
  await page.getByRole('button', { name: 'Request changes' }).click();
  await page.getByLabel('What should change?').fill('Please include the invoice date.');
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByLabel('What should change?')).toHaveCount(0);
  await page.getByRole('button', { name: 'Request changes' }).click();
  await page.getByLabel('What should change?').fill('Please include the invoice date.');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.getByText('You asked: Please include the invoice date.')).toBeVisible();
  await expect(page.getByText('Attempt 1')).toBeVisible();
  await expect(page.getByText('1 more change request left.')).toBeVisible();
  await page.getByRole('button', { name: 'Request changes' }).click();
  await page.getByLabel('What should change?').fill('Check the total again.');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.getByText('No change requests left.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Request changes' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect
    .poll(() => commands.map((command) => command.decision))
    .toEqual(['request_changes', 'request_changes', 'approve']);
  expect(commands[0]?.reason).toBe('Please include the invoice date.');
  expect(commands[1]?.reason).toBe('Check the total again.');
  expect(commands[2]?.reason).toBeUndefined();
});

test('a paused run with no open review offers Stop run', async ({ page }) => {
  const definition = typedResultsDefinition();
  const now = new Date().toISOString();
  const instance = {
    id: 'run-1',
    workflowId: 'wf-canvas-e2e',
    definitionRevision: 1,
    ownerUserId: 'owner',
    status: 'paused',
    stopRequested: false,
    currentNodeId: 'accounts',
    envelope: { title: 'Invoice approval' },
    envelopeHash: 'hash',
    envelopeRevision: 1,
    snapshot: definition,
    steps: [],
    createdAt: now,
    updatedAt: now,
    startedAt: now,
  };
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/setup/status')) return route.fulfill({ json: { needsSetup: false } });
    if (path.endsWith('/auth/refresh'))
      return route.fulfill({ json: { accessToken: 'e2e-token', expiresInSeconds: 3600 } });
    if (path.endsWith('/auth/me'))
      return route.fulfill({ json: { id: 'owner', role: 'Admin', userName: 'Owner' } });
    if (path.endsWith('/settings/agents')) return route.fulfill({ json: agents });
    if (path.endsWith('/runtime/execution-readiness'))
      return route.fulfill({
        json: { ready: true, code: 'READY', message: 'Ready', dependencies: [] },
      });
    if (path.endsWith('/approvals/pending')) return route.fulfill({ json: [] });
    if (path.endsWith('/workflows/wf-canvas-e2e/instances/run-1/events'))
      return route.fulfill({
        json: {
          events: [
            {
              eventId: 'paused-event',
              type: 'workflow.paused',
              nodeId: 'accounts',
              timestamp: now,
            },
          ],
        },
      });
    if (path.endsWith('/workflows/wf-canvas-e2e/instances/run-1'))
      return route.fulfill({ json: instance });
    if (path.endsWith('/workflows/wf-canvas-e2e')) return route.fulfill({ json: definition });
    if (path.endsWith('/workflows/wf-canvas-e2e/instances/run-1/stop'))
      return route.fulfill({ json: { ...instance, status: 'stopped' } });
    return route.fulfill({ json: {} });
  });

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/workflows/wf-canvas-e2e/instances/run-1');
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeHidden();
  await expect(page.getByText('Paused', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('This run is paused.')).toBeVisible();
  await expect(page.getByText(/ready for you to check/i)).toHaveCount(0);
  await page.getByRole('button', { name: 'Stop run' }).click();
});

test('canvas follows dark mode colors', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByLabel('Who does the first step?').selectOption('reviewer');
  await page.getByRole('button', { name: 'Add first step' }).click();
  await page.locator('html').evaluate((element) => element.classList.add('dark'));
  const background = await page
    .locator('.workflow-canvas-surface')
    .evaluate((element) => getComputedStyle(element).backgroundColor);
  const box = await page
    .locator('.workflow-canvas-box')
    .last()
    .evaluate((element) => getComputedStyle(element).backgroundColor);
  expect(background).not.toBe('rgb(255, 255, 255)');
  expect(box).not.toBe('rgb(255, 255, 255)');
});

test('editor opens with its list hidden and never carries that choice to other pages', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await expect(page.getByRole('button', { name: 'Show workflows' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toHaveCount(0);
  const surface = page.locator('.workflow-canvas-surface');
  expect((await surface.boundingBox())?.width).toBeGreaterThan(850);

  await page.goto('/workflows');
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
  await page.locator('button[title="Invoice approval"]').click();
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeVisible();
  await page.getByRole('button', { name: 'Edit workflow' }).click();
  await expect(page.getByRole('button', { name: 'Show workflows' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toHaveCount(0);
});

test('workflow list returns after navigating away and the editor can show it again', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openEditor(page);
  await page.getByRole('button', { name: 'Show workflows' }).click();
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
  await page.getByRole('button', { name: 'Hide workflows' }).click();
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toHaveCount(0);

  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  await page.goto('/workflows');
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();

  await page.locator('button[title="Invoice approval"]').click();
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeVisible();
  await page.getByRole('button', { name: 'Edit workflow' }).click();
  await expect(page.getByRole('button', { name: 'Show workflows' })).toBeVisible();
  await page.getByRole('button', { name: 'Show workflows' }).click();
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();

  await page.reload();
  await expect(page.getByRole('button', { name: 'Show workflows' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toHaveCount(0);
});

test('one Back after saving returns to the workflow list', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const editor = await openEditor(page, typedResultsDefinition());
  await page.goto('/workflows');
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
  await page.locator('button[title="Invoice approval"]').click();
  await expect(page.getByRole('button', { name: 'Edit workflow' })).toBeVisible();
  await page.getByRole('button', { name: 'Edit workflow' }).click();
  await expect(page.getByRole('button', { name: 'Save workflow' })).toBeVisible();
  await selectStep(page, 'Step 1 · Accounts assistant');
  await page.getByLabel('What should they do?').fill('Review the invoice carefully.');
  await page.getByRole('button', { name: 'Save workflow' }).click();
  await expect
    .poll(() => editor.getDefinition().nodes[0]?.promptTemplate)
    .toBe('Review the invoice carefully.');
  await expect(page.getByText('Saved.')).toBeVisible();

  await page.goBack({ waitUntil: 'commit' });
  await expect(page).toHaveURL(/\/workflows\/wf-canvas-e2e$/);
  await expect(page.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
});

test('delete an unrun workflow, archive one with runs, then restore it', async ({ page }) => {
  const makeDefinition = (id: string, name: string, hasRuns: boolean) => ({
    ...blankDefinition(),
    id,
    name,
    hasRuns,
    archived: false,
    nodes: [
      {
        nodeId: 'review',
        agentId: 'reviewer',
        promptTemplate: 'Review the request.',
        outputKind: 'schema',
        outputSchema: { type: 'object', properties: {} },
        maps: [],
        sideEffects: 'none',
        memoryWrites: false,
        requiresReview: false,
        maxRetries: 0,
        switchCases: [],
        defaultGoTo: null,
      },
    ],
  });
  const unrun = makeDefinition('wf-unrun', 'Title polish', false);
  const withRuns = makeDefinition('wf-has-runs', 'Invoice follow-up', true);
  let archived = false;
  let deletedUnrun = false;
  let rejectedArchive = false;
  let archiveRequests = 0;
  const confirmations: string[] = [];
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    const method = route.request().method();
    if (path.endsWith('/auth/setup/status')) return route.fulfill({ json: { needsSetup: false } });
    if (path.endsWith('/auth/refresh'))
      return route.fulfill({ json: { accessToken: 'e2e-token', expiresInSeconds: 3600 } });
    if (path.endsWith('/auth/me'))
      return route.fulfill({ json: { id: 'owner', role: 'Admin', userName: 'Owner' } });
    if (path.endsWith('/settings/agents')) return route.fulfill({ json: agents });
    if (path.endsWith('/runtime/execution-readiness'))
      return route.fulfill({
        json: { ready: true, code: 'READY', message: 'Ready', dependencies: [] },
      });
    if (path.endsWith('/workflows/wf-unrun') && method === 'DELETE') {
      deletedUnrun = true;
      return route.fulfill({ status: 204 });
    }
    if (path.endsWith('/workflows/wf-has-runs/archive') && method === 'POST') {
      archiveRequests += 1;
      if (!rejectedArchive) {
        rejectedArchive = true;
        return route.fulfill({
          status: 409,
          json: { detail: 'This workflow has a run in progress. Stop it first.' },
        });
      }
      archived = true;
      withRuns.nodes[0]!.agentId = 'removed-agent';
      return route.fulfill({ status: 204 });
    }
    if (path.endsWith('/workflows/wf-has-runs/unarchive') && method === 'POST') {
      archived = false;
      return route.fulfill({ status: 204 });
    }
    if (path.endsWith('/workflows/wf-unrun')) return route.fulfill({ json: unrun });
    if (path.endsWith('/workflows/wf-has-runs/instances'))
      return route.fulfill({
        json: [
          {
            id: 'run-kept',
            workflowId: withRuns.id,
            definitionRevision: 1,
            ownerUserId: 'owner',
            correlationKey: null,
            status: 'finished',
            stopRequested: false,
            currentNodeId: null,
            envelope: {},
            envelopeHash: 'hash',
            envelopeRevision: 1,
            snapshot: withRuns,
            steps: [],
            failureCode: null,
            error: null,
            createdAt: '2026-10-24T00:00:00Z',
            updatedAt: '2026-10-24T00:00:00Z',
            startedAt: null,
            endedAt: '2026-10-24T00:01:00Z',
          },
        ],
      });
    if (path.endsWith('/workflows/wf-has-runs'))
      return route.fulfill({ json: { ...withRuns, archived } });
    if (path.endsWith('/workflows')) {
      if (url.searchParams.get('archived') === 'true')
        return route.fulfill({ json: archived ? [{ ...withRuns, archived: true }] : [] });
      return route.fulfill({
        json: [...(deletedUnrun ? [] : [unrun]), ...(archived ? [] : [withRuns])],
      });
    }
    return route.fulfill({ json: {} });
  });
  page.on('dialog', (dialog) => {
    confirmations.push(dialog.message());
    void dialog.accept();
  });
  await page.goto('/workflows');
  const unselectedWorkflow = page
    .getByRole('navigation', { name: 'Workflows and runs' })
    .locator('button[title="Invoice follow-up"]');
  await unselectedWorkflow.hover();
  const unselectedActions = page.getByRole('button', {
    name: 'Workflow actions for Invoice follow-up',
  });
  await expect(unselectedActions).toBeVisible();
  await unselectedActions.click();
  await expect(page.getByRole('menu')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('menu')).toHaveCount(0);
  await unselectedActions.click();
  await expect(page.getByRole('menu')).toBeVisible();
  await page.mouse.click(1000, 500);
  await expect(page.getByRole('menu')).toHaveCount(0);

  await page.goto('/workflows/wf-unrun');
  await page
    .getByRole('navigation', { name: 'Workflows and runs' })
    .getByRole('button', { name: 'Workflow actions for Title polish' })
    .click();
  await page.getByRole('menuitem', { name: 'Delete workflow' }).click();
  expect(confirmations[0]).toBe("Delete 'Title polish'? This can't be undone.");
  await expect(page.locator('.page-title', { hasText: 'Workflows' })).toBeVisible();
  await expect(
    page
      .getByRole('navigation', { name: 'Workflows and runs' })
      .getByRole('button', { name: 'Title polish' })
  ).toHaveCount(0);

  await page.goto('/workflows/wf-has-runs');
  await page
    .getByRole('navigation', { name: 'Workflows and runs' })
    .getByRole('button', { name: 'Workflow actions for Invoice follow-up' })
    .click();
  await page.getByRole('menuitem', { name: 'Archive workflow' }).click();
  await expect.poll(() => archiveRequests).toBe(1);
  expect(archived).toBe(false);
  expect(confirmations[1]).toBe(
    "Archive 'Invoice follow-up'? It won't run any more, but its past runs stay. You can restore it any time."
  );
  await expect(
    page.getByRole('navigation', { name: 'Workflows and runs' }).getByRole('alert')
  ).toContainText('This workflow has a run in progress. Stop it first.');
  await page
    .getByRole('navigation', { name: 'Workflows and runs' })
    .getByRole('button', { name: 'Workflow actions for Invoice follow-up' })
    .click();
  await page.getByRole('menuitem', { name: 'Archive workflow' }).click();
  expect(confirmations[2]).toBe(confirmations[1]);
  await expect(page.getByText('Archived (1)')).toBeVisible();
  await page.getByText('Archived (1)').click();
  await page.getByRole('button', { name: 'Invoice follow-up' }).click();
  await expect(page.getByRole('heading', { name: 'Invoice follow-up' })).toBeVisible();
  await expect(page.locator('article')).toContainText('Agent removed');
  await expect(page.locator('article')).toContainText('Review the request.');
  await expect(page.locator('a[href$="/instances/run-kept"]')).toBeVisible();
  await page.getByRole('button', { name: 'Restore' }).click();
  await page.goto('/workflows/wf-has-runs/edit');
  await page.reload();
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Workflow name' })).toHaveValue(
    'Invoice follow-up'
  );
  await expect(
    page.getByRole('button', { name: 'Workflow actions for Invoice follow-up' })
  ).toBeVisible();
});
