import { describe, expect, it } from 'vitest';
import { validateSlackAppToken, validateSlackBotToken } from '@/features/auth/components/slackTokenValidation';

describe('Slack token fields', () => {
  it('accepts only app-level tokens in the app token field', () => {
    expect(validateSlackAppToken('xapp-valid')).toBe(true);
    expect(validateSlackAppToken('xoxb-wrong-field')).toBe(false);
  });

  it('accepts only bot tokens in the bot token field', () => {
    expect(validateSlackBotToken('xoxb-valid')).toBe(true);
    expect(validateSlackBotToken('xapp-wrong-field')).toBe(false);
  });
});
