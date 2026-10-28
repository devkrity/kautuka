# Providers

A provider tells Gateway where an AI service is and how to authenticate to it. Configure providers in **Settings → Providers**.

## Supported provider types

- Ollama: base URL required; no API key.
- OpenAI, Anthropic, Groq, Mistral, OpenRouter, and Gemini: API key required; a default service URL is supplied.
- Azure OpenAI and Azure AI Foundry: base URL and API key required.

The live catalog is authoritative: `GET /api/settings/provider-types`. Administrators can adjust defaults for newly created providers under **Settings → Advanced → Provider Catalog**.

## Safe setup

1. Choose a provider type and a unique provider ID.
2. Enter the required base URL and credential.
3. Use **Test** before saving.
4. Save, then continue to Models.

Credentials are submitted separately from the public provider projection. Listing providers does not return secret values.

For Ollama, the URL must be reachable from the Gateway process, not merely from the browser. The normal default is `http://localhost:11434`; if Gateway and Ollama run in different host or VM network contexts, use an address reachable from Gateway. Discovery calls Ollama’s `/api/tags`; model runs use OllamaSharp against the native `/api/chat` endpoint (thinking and tools map into MEAI content types).

## Endpoints and verification

- List: `GET /api/settings/providers`
- Types: `GET /api/settings/provider-types`
- Test: `POST /api/settings/providers/test`
- Save/delete: `POST /api/settings/providers/save`, `DELETE /api/settings/providers/{providerId}`

Verify that Test succeeds and model discovery returns results. A provider cannot be deleted while dependent configuration still references it.
