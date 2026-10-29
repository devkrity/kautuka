import { expect, test } from '@playwright/test';
import { readCreatedThreadId } from '../src/thread-creation.js';

test('create-thread correlation accepts only the returned canonical id', () => {
  expect(readCreatedThreadId({ id: 'session-created', name: 'not retained by the flow' })).toBe('session-created');
  expect(() => readCreatedThreadId({ threadId: 'wrong-shape' })).toThrow(/did not contain a thread id/);
  expect(() => readCreatedThreadId({ id: '' })).toThrow(/did not contain a thread id/);
});
