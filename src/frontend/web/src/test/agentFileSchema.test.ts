import { agentFileListResponseSchema } from '@/features/agent-files/schemas/agentFileSchema';
import { describe, expect, it } from 'vitest';

describe('agentFileListResponseSchema', () => {
  it('accepts DateTimeOffset timestamps returned by the backend', () => {
    const parsed = agentFileListResponseSchema.parse({
      agentId: 'agent-1',
      path: '',
      entries: [
        {
          name: 'reports',
          path: 'reports',
          entryType: 'directory',
          sizeBytes: null,
          modifiedAt: '2026-05-20T10:00:00+00:00',
          fileCount: 3,
        },
      ],
      totalFileCount: 3,
      totalSizeBytes: 4096,
    });

    expect(parsed.entries[0]?.modifiedAt).toBe('2026-05-20T10:00:00+00:00');
    expect(parsed.entries[0]?.fileCount).toBe(3);
    expect(parsed.totalFileCount).toBe(3);
    expect(parsed.totalSizeBytes).toBe(4096);
  });
});