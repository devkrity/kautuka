import { expect, test } from '@playwright/test';
import { readImportedSkillId } from '../src/skill-fixture-bootstrap.js';

test('public skill bootstrap accepts only the exact single roll-dice import identifier', () => {
  expect(readImportedSkillId({ type: 'single', single: { skill: { id: 'roll-dice' } } })).toBe('roll-dice');
  expect(() => readImportedSkillId({ type: 'bundle', bundle: { skills: [] } })).toThrow(/exact expected skill id/i);
  expect(() => readImportedSkillId({ type: 'single', single: { skill: { id: 'other' } } })).toThrow(/exact expected skill id/i);
});
