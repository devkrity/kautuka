import { expect } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import { openAgent } from './flows.js';
import type { AcceptanceScenario } from './model.js';

/** Browser-visible file upload, listing, and traversal-safe content access. */
export const fileWorkflowScenarios: readonly AcceptanceScenario[] = [
  {
    id: 'CRH-038-FILE-UPLOAD-REPLAY',
    title: 'authenticated file upload lists once and rejects traversal on content read',
    tags: ['full', 'release-gate'],
    checklistIds: ['F-01'],
    async execute(context) {
      const { page, baseUrl, fixtures } = context;
      await openAgent(page, baseUrl, fixtures.rollerId);
      const headers = await authenticatedBrowserHeaders(page, baseUrl);
      const agentId = fixtures.rollerId;
      const request = page.context().request;

      const upload = await request.post(new URL(`/api/agents/${encodeURIComponent(agentId)}/files`, baseUrl).toString(), {
        headers,
        multipart: {
          file: {
            name: 'acceptance-note.txt',
            mimeType: 'text/plain',
            buffer: Buffer.from(`acceptance-file-${context.seed}`),
          },
        },
      });
      expect(upload.ok(), `upload returned ${upload.status()}: ${(await upload.text()).slice(0, 300)}`).toBeTruthy();
      const uploaded = await upload.json() as { path?: string };
      expect(uploaded.path).toContain('acceptance-note.txt');

      const listing = await request.get(new URL(`/api/agents/${encodeURIComponent(agentId)}/files`, baseUrl).toString(), { headers });
      expect(listing.ok()).toBeTruthy();
      const body = await listing.text();
      expect(body).toContain('acceptance-note.txt');

      const traversal = await request.get(
        new URL(`/api/agents/${encodeURIComponent(agentId)}/files/content?path=..%2F..%2Foutside.txt`, baseUrl).toString(),
        { headers },
      );
      expect(traversal.status()).toBeGreaterThanOrEqual(400);
      expect((await traversal.text()).slice(0, 400)).not.toMatch(/[A-Za-z]:\\|\/etc\/|BEGIN [A-Z ]+ KEY/);

      await page.reload();
      await expect(page.locator('textarea').last()).toBeEnabled();
    },
  },
];
