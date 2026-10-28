export function validateSlackAppToken(token: string): boolean {
  const value = token.trim();
  return value.length === 0 || value.startsWith('xapp-');
}

export function validateSlackBotToken(token: string): boolean {
  const value = token.trim();
  return value.length === 0 || value.startsWith('xoxb-');
}
