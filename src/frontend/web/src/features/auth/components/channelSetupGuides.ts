export type ChannelSetupStep = {
  title: string;
  detail: string;
};

export type ChannelSetupGuide = {
  intro: string;
  destinationLabel: string;
  destinationHelp: string;
  portalLabel: string;
  portalUrl: string;
  portalRequiresAuth?: boolean;
  steps: ChannelSetupStep[];
  automated: string[];
  afterSave: string;
  setupArtifact?: {
    label: string;
    content: string;
  };
};

const fallbackGuide: ChannelSetupGuide = {
  intro: 'Connect this provider, choose an agent, then verify delivery with a test prompt.',
  destinationLabel: 'Default destination ID',
  destinationHelp:
    'Optional. Used for test prompts and approval delivery when no conversation has been bound yet.',
  portalLabel: 'Open provider setup',
  portalUrl: 'https://github.com/kautuka/kautuka',
  steps: [
    {
      title: 'Create or choose a bot',
      detail: 'Use the provider portal to create a bot and grant only the permissions it needs.',
    },
    {
      title: 'Paste the credentials below',
      detail: 'Kautuka stores secret fields in its credential service and never displays them again.',
    },
    {
      title: 'Choose an agent and connect',
      detail: 'Kautuka will provision the registration and report whether the connection succeeded.',
    },
  ],
  automated: ['Credential isolation', 'Registration provisioning', 'Connection verification'],
  afterSave: 'Send a test prompt to verify end-to-end delivery.',
};

const guides = new Map<string, ChannelSetupGuide>([
  [
    'telegram',
    {
      intro: 'Telegram is the quickest setup: create a bot in BotFather and paste its token here.',
      destinationLabel: 'Telegram private chat ID',
      destinationHelp:
        'Required. This is the numeric ID of your private chat with the Kautuka bot, where it sends replies and approval requests.',
      portalLabel: 'Open BotFather',
      portalUrl: 'https://t.me/BotFather',
      steps: [
        {
          title: 'Create the bot',
          detail:
            'Open BotFather, send /newbot, choose a display name and a unique username ending in bot.',
        },
        {
          title: 'Paste the bot token',
          detail:
            'Copy the token BotFather returns. Treat it like a password; paste it only into the secret field below.',
        },
        {
          title: 'Find the Telegram private chat ID',
          detail:
            'Open your private chat with the bot and send it a message. Use Telegram’s official Bot API getUpdates method with your bot token, then copy the message.chat.id value into the Telegram private chat ID field. Do not give the token to a third-party ID bot or website.',
        },
      ],
      automated: [
        'Secure token storage',
        'Bot-token validation',
        'Command menu registration',
        'Long-poll connection and reconnection',
      ],
      afterSave:
        'Select Save & Connect, then Send Test Prompt to verify delivery to the private chat ID you entered.',
    },
  ],
  [
    'slack',
    {
      intro: 'Slack connects as a private assistant for one Kautuka member per workspace. Use it only in your direct message with the app; it never posts to channels.',
      destinationLabel: '',
      destinationHelp: '',
      portalLabel: 'Create Slack app',
      portalUrl: '/api/settings/channels/slack/create-app',
      portalRequiresAuth: true,
      steps: [
        {
          title: 'Create your Slack app',
          detail: 'Choose your workspace, then click Create. Kautuka fills in the private DM-only app settings for you and refuses apps with broader channel scopes.',
        },
        {
          title: 'Generate the app-level token (xapp-)',
          detail: 'In your app dashboard, click Basic Information in the left menu and scroll to App-Level Tokens. Click Generate Token and Give it a name. In the scope picker, add connections:write, click Generate, then copy the xapp- token. Paste it into App-level token below.',
        },
        {
          title: 'Install the app and copy the bot token (xoxb-)',
          detail: 'In the app dashboard, click OAuth & Permissions in the left menu. If the app is not installed, click Install to Workspace at the top and approve it; if already installed, choose Reinstall to Workspace if Slack prompts you. Return to OAuth & Permissions, find Bot User OAuth Token, and copy the xoxb- token into Bot user token below.',
        },
        {
          title: 'Paste both tokens and connect',
          detail: 'Check that the xapp- token is in App-level token and the xoxb- token is in Bot user token, then click Check tokens and connect.',
        },
        {
          title: 'Link yourself to the bot',
          detail: 'After Kautuka shows a code, open the app in Slack, start a direct message with the bot, and send the code. Keep this page open until it confirms your account is linked.',
        },
      ],
      automated: [
        'Keeps both tokens in its secure credential service',
        'Checks Slack can reach your bot and start a live connection',
        'Links your direct message without asking you to type an ID',
      ],
      afterSave: 'When linked, send a test message to confirm everything works.',
    },
  ],
]);

export function getChannelSetupGuide(providerKey: string): ChannelSetupGuide {
  return guides.get(providerKey.toLowerCase()) ?? fallbackGuide;
}

type ChannelSetupResult = {
  success: boolean;
  status: string;
  detail?: string | null;
} | null | undefined;

export function getChannelSetupOutcome(
  displayName: string,
  setup: ChannelSetupResult
): { success: boolean; message: string } {
  if (!setup) {
    return { success: true, message: `${displayName} settings saved.` };
  }

  const status = setup.status.toLowerCase();
  const detail = setup.detail?.toLowerCase() ?? '';
  if (displayName.toLowerCase() === 'slack' && detail.includes('slack_workspace_in_use')) {
    return {
      success: false,
      message: 'This Slack workspace is already connected to another Kautuka member. For now each workspace can be connected by one member.',
    };
  }
  if (setup.success) {
    return status === 'accepted' || status === 'running'
      ? {
          success: true,
          message: `${displayName} settings saved. Connection setup is still starting; verify it with a test prompt in a moment.`,
        }
      : {
          success: true,
          message: `${displayName} saved and connected. Complete the final provider step below, then verify with a test prompt.`,
        };
  }

  if (
    detail.includes('missing_bot_token') ||
    detail.includes('missing_app_token') ||
    detail.includes('token_required')
  ) {
    return {
      success: false,
      message: `${displayName} was saved, but a required token is missing. Paste the token requested in the guide and select Save & Connect again.`,
    };
  }

  if (displayName.toLowerCase() === 'slack' && detail.includes('invalid_bot_token_type_expected_xoxb')) {
    return {
      success: false,
      message: 'This is an app-level token. Paste the xapp- token in the app token box; the bot token starts with xoxb-.',
    };
  }

  if (displayName.toLowerCase() === 'slack' && detail.includes('invalid_app_token_type_expected_xapp')) {
    return {
      success: false,
      message: 'This is a bot token. Paste the xoxb- token in the bot token box; the app-level token starts with xapp-.',
    };
  }

  if (
    displayName.toLowerCase() === 'slack' &&
    detail.includes("doesn't match kautuka's private setup")
  ) {
    return {
      success: false,
      message: "This Slack app doesn't match Kautuka's private setup. Recreate it from the manifest link.",
    };
  }

  if (displayName.toLowerCase() === 'slack' && detail.includes('request_to_install')) {
    return {
      success: false,
      message: 'Your workspace needs an administrator to approve this Slack app before it can connect.',
    };
  }

  if (
    detail.includes('invalid_auth') ||
    detail.includes('unauthorized') ||
    detail.includes('token_revoked') ||
    detail.includes('401')
  ) {
    return {
      success: false,
      message: `${displayName} rejected the credential. Generate or copy a fresh token from the official portal, then select Save & Connect again.`,
    };
  }

  if (detail.includes('missing_scope') || detail.includes('forbidden') || detail.includes('403')) {
    return {
      success: false,
      message: `${displayName} connected but lacks a required permission. Review the permission step in the guide, reinstall the bot if required, then reconnect.`,
    };
  }

  if (detail.includes('timeout') || detail.includes('unavailable') || detail.includes('network')) {
    return {
      success: false,
      message: `${displayName} could not be reached. Check internet access and try Save & Connect again; your stored credential remains concealed.`,
    };
  }

  return {
    success: false,
    message: `${displayName} settings were saved, but Kautuka could not verify the connection. Recheck the provider steps and permissions, then reconnect.`,
  };
}
