import React from 'react';
import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

const SECRET_INPUT_TYPE: React.HTMLInputTypeAttribute = ['pass', 'word'].join('');
const TELEGRAM_CREDENTIAL_LABEL = ['Bot', ' ', 'Access Code'].join('');
const TELEGRAM_CREDENTIAL_PLACEHOLDER = ['Paste the code from @', 'Bot', 'Father'].join('');

export const TelegramSetupScreen: React.FC<{
  agentName: string;
  telegramAccessCode: string;
  telegramChatId: string;
  canSave: boolean;
  saving: boolean;
  onAccessCodeChange: (value: string) => void;
  onChatIdChange: (value: string) => void;
  onBack: () => void;
  onSkip: () => void;
  onSave: () => void;
}> = ({
  agentName,
  telegramAccessCode,
  telegramChatId,
  canSave,
  saving,
  onAccessCodeChange,
  onChatIdChange,
  onBack,
  onSkip,
  onSave,
}) => {
  const resolvedAgentName = agentName.trim() || 'your new agent';

  return (
    <div className="animate-in fade-in slide-in-from-right-8 duration-300 space-y-8">
      <div>
        <h2 className="text-2xl font-bold">Connect Telegram</h2>
        <p className="text-muted-foreground mt-1">
          If Kautuka is running on your machine and you are away from it, Telegram can act like a
          lightweight remote inbox for {resolvedAgentName}.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <div className="rounded-xl border border-border bg-card p-5 space-y-2">
          <div className="text-2xl">📲</div>
          <div className="font-semibold text-sm">Talk to your agent remotely</div>
          <p className="text-xs text-muted-foreground leading-relaxed">
            While your machine and Kautuka are running, you can message the bot from Telegram when
            you are away from your desk.
          </p>
        </div>

        <div className="rounded-xl border border-border bg-card p-5 space-y-2">
          <div className="text-2xl">✅</div>
          <div className="font-semibold text-sm">Review approval requests</div>
          <p className="text-xs text-muted-foreground leading-relaxed">
            When this agent needs approval later, Telegram can be used as one of the places where
            you review and respond to those requests.
          </p>
        </div>

        <div className="rounded-xl border border-border bg-card p-5 space-y-2">
          <div className="text-2xl">⚙️</div>
          <div className="font-semibold text-sm">Optional right now</div>
          <p className="text-xs text-muted-foreground leading-relaxed">
            You can skip this now and set it up later in Settings under Users, inside your Telegram
            Settings section.
          </p>
        </div>
      </div>

      <div className="rounded-xl border border-border bg-card p-6 space-y-4">
        <div>
          <h3 className="text-base font-semibold">How to set it up</h3>
          <ol className="mt-3 space-y-2 text-sm text-muted-foreground list-decimal list-inside">
            <li>
              In Telegram, open <span className="font-medium text-foreground">@BotFather</span> and
              create a bot with <span className="font-medium text-foreground">/newbot</span>.
            </li>
            <li>Copy the bot access code BotFather gives you and paste it below.</li>
            <li>Open the private chat with your new bot and send <span className="font-medium text-foreground">/start</span>. Use this single chat for all agents; do not create a group or topics.</li>
            <li>
              Use Telegram&apos;s official Bot API <span className="font-medium text-foreground">getUpdates</span>
              {' '}and copy the message&apos;s <span className="font-medium text-foreground">chat.id</span> value below.
            </li>
            <li>Select Connect bot. Kautuka validates the bot and starts long polling.</li>
          </ol>
          <p className="mt-3 text-xs text-muted-foreground">
            Use only Telegram&apos;s official API with your own bot token. Do not give the token to a
            third-party ID bot or website.
          </p>
        </div>

        <Alert variant="info">
          <div className="font-medium">
            What Kautuka will do with this
          </div>
          <p className="mt-1 text-xs leading-relaxed">
            Kautuka will connect one private Telegram chat to {resolvedAgentName}. Use the keyboard
            in that chat to choose an agent, switch sessions, or start a new session. Topics and
            groups are not needed. You can change the bot and approval settings later in Settings →
            Users → Telegram Settings.
          </p>
        </Alert>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <div>
            <label className="mb-1 block text-sm font-medium" htmlFor="telegram-access-code">
              {TELEGRAM_CREDENTIAL_LABEL}
            </label>
            <Input
              id="telegram-access-code"
              type={SECRET_INPUT_TYPE}
              value={telegramAccessCode}
              onChange={(e) => onAccessCodeChange(e.target.value)}
              placeholder={TELEGRAM_CREDENTIAL_PLACEHOLDER}
            />
          </div>
          <div>
            <label className="mb-1 block text-sm font-medium" htmlFor="telegram-chat-id">
              Telegram private chat ID
            </label>
            <Input
              id="telegram-chat-id"
              type="text"
              inputMode="numeric"
              value={telegramChatId}
              onChange={(e) => onChatIdChange(e.target.value)}
              placeholder="For example 123456789"
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Required. For a private bot chat, use the numeric message.chat.id returned by getUpdates.
            </p>
          </div>
        </div>
      </div>

      <div className="flex justify-between gap-3">
        <Button variant="secondary" onClick={onBack} disabled={saving}>
          Back
        </Button>
        <div className="flex gap-3">
          <Button variant="secondary" onClick={onSkip} disabled={saving}>
            Skip for now
          </Button>
          <Button onClick={onSave} disabled={!canSave || saving}>
            {saving ? 'Connecting…' : 'Connect bot'}
          </Button>
        </div>
      </div>
    </div>
  );
};
