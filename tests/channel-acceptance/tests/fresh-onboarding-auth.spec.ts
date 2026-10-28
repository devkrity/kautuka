import { expect, test } from '@playwright/test';
import { loginAuthorization } from '../src/acceptance-login.js';

test('fresh onboarding uses only the normal browser login response for public fixture authorization', () => {
  expect(loginAuthorization({ accessToken: 'browser-issued-token' })).toEqual({ Authorization: 'Bearer browser-issued-token' });
  expect(() => loginAuthorization({})).toThrow(/Normal browser login/);
  expect(() => loginAuthorization({ accessToken: ' ' })).toThrow(/Normal browser login/);
});
