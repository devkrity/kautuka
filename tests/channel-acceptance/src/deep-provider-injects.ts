export function slackSlash(destination: string, command: string, eventId: string) {
  return {
    provider: 'slack' as const,
    kind: 'command' as const,
    actorReference: 'U1',
    conversationReference: destination,
    payload: {
      _simulatorEnvelopeType: 'slash_commands',
      envelope_id: eventId,
      payload: { user_id: 'U1', channel_id: destination, command, text: '' },
    },
  };
}

export function telegramFileMessage(destination: string, caption: string, eventId: string) {
  return {
    provider: 'telegram' as const,
    kind: 'file' as const,
    actorReference: destination,
    conversationReference: destination,
    payload: {
      message: {
        message_id: Number(eventId.replace(/\D/g, '').slice(-6) || '2'),
        date: 0,
        chat: { id: Number(destination), type: 'private' },
        from: { id: Number(destination), is_bot: false, first_name: 'Acceptance' },
        caption,
        document: { file_id: `file-${eventId}`, file_name: 'acceptance-note.txt', mime_type: 'text/plain', file_size: 12 },
      },
    },
  };
}
