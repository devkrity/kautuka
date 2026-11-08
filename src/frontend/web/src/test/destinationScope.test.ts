import { describe, expect, it } from 'vitest';
import { destinationScope } from '@/features/network-access/destinationScope';

describe('network destination guidance', () => {
  it.each([
    ['api.openai.com', 'public'],
    ['8.8.8.8', 'public'],
    ['192.0.0.9', 'public'],
    ['192.0.0.10', 'public'],
    ['192.168.1.20', 'private'],
    ['10.0.0.1', 'private'],
    ['100.64.0.1', 'private'],
    ['192.0.2.10', 'private'],
    ['169.254.169.253', 'private'],
    ['169.254.169.254', 'metadata'],
    ['169.254.170.23', 'metadata'],
    ['metadata.google.internal.', 'metadata'],
    ['100.100.100.200', 'metadata'],
    ['168.63.129.16', 'blocked'],
    ['192.0.0.8', 'blocked'],
    ['127.0.0.1', 'blocked'],
    ['0.1.2.3', 'blocked'],
    ['224.0.0.1', 'blocked'],
  ] as const)('describes %s as %s', (destination, expected) =>
    expect(destinationScope(destination)).toBe(expected)
  );
});
