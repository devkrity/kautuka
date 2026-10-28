import { expect, test } from '@playwright/test';
import { parseServiceFindings } from '../src/service-log-findings.js';

test('service findings use structured log headers, retain bounded context, and sanitize it', () => {
  const findings = parseServiceFindings('ChannelHost', [
    'info: polling complete success=True error=none',
    '[ChannelHost] warn: polling delayed',
    'detail token=private-value',
    'at Provider.Worker()',
    'fail: durable continuation failed',
  ].join('\n'));
  expect(findings).toEqual([
    { service: 'ChannelHost', level: 'warning', message: '[ChannelHost] warn: polling delayed\ndetail [REDACTED]\nat Provider.Worker()' },
    { service: 'ChannelHost', level: 'error', message: 'fail: durable continuation failed' },
  ]);
});
