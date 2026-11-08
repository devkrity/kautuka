from __future__ import annotations

import pytest
from jsonschema import validate

from kautuka_web_tools import pipeline
from kautuka_web_tools.pipeline import FetchResult
from kautuka_web_tools.server import (
    PROMPT_NAME,
    SERVER_INSTRUCTIONS,
    TOOL_NAME,
    handle_call_tool,
    handle_get_prompt,
    handle_list_prompts,
    handle_list_tools,
)


@pytest.mark.asyncio
@pytest.mark.parametrize("content_type", ["text/html", "text/plain"])
@pytest.mark.parametrize("response_mode", ["compact", "full"])
async def test_successful_fetch_matches_advertised_output_schema(monkeypatch: pytest.MonkeyPatch, content_type: str, response_mode: str) -> None:
    body = b"<html><body><h1>Fixture</h1><p>Evidence text.</p></body></html>" if content_type == "text/html" else b"Evidence text."

    async def fake_fetch(url: str, options: pipeline.FetchOptions) -> FetchResult:
        return FetchResult(url, url, 200, content_type, "2026-11-09T00:00:00Z", "http", 0, len(body), body, {})

    monkeypatch.setattr(pipeline, "fetch_url", fake_fetch)
    result = await handle_call_tool(TOOL_NAME, {"url": "https://example.com", "fetchMode": "http", "responseMode": response_mode})
    assert not result.isError
    assert result.structuredContent is not None
    tool = (await handle_list_tools())[0]
    assert tool.outputSchema is not None
    validate(result.structuredContent, tool.outputSchema)


@pytest.mark.asyncio
async def test_list_tools_publishes_mcp_metadata_contract() -> None:
    tools = await handle_list_tools()

    assert len(tools) == 1

    tool = tools[0]
    assert tool.name == TOOL_NAME
    assert tool.title == "Web Evidence Fetch"
    assert tool.annotations is not None
    assert tool.annotations.readOnlyHint is True
    assert tool.annotations.destructiveHint is False
    assert tool.annotations.idempotentHint is True
    assert tool.annotations.openWorldHint is True
    assert tool.outputSchema is not None
    assert tool.outputSchema["additionalProperties"] is False
    assert set(tool.outputSchema["required"]) >= {
        "fetch",
        "normalization",
        "document",
        "guidance",
        "items",
        "selectedChunks",
        "warnings",
    }

    guidance_schema = tool.outputSchema["properties"]["guidance"]
    follow_up_arguments_schema = guidance_schema["properties"]["followUpCalls"]["items"]["properties"]["arguments"]
    items_schema = tool.outputSchema["properties"]["items"]["items"]
    chunk_window_schema = tool.outputSchema["properties"]["chunkWindow"]

    assert guidance_schema["additionalProperties"] is False
    assert follow_up_arguments_schema["additionalProperties"] is False
    assert items_schema["additionalProperties"] is False
    assert items_schema["required"] == ["itemId", "title", "url", "sectionPath", "sectionId"]
    assert chunk_window_schema["additionalProperties"] is False
    assert chunk_window_schema["properties"]["nextOptions"]["anyOf"][0]["additionalProperties"] is False

    assert "selectedChunks" in SERVER_INSTRUCTIONS
    assert "responseMode='full'" in SERVER_INSTRUCTIONS


@pytest.mark.asyncio
async def test_prompt_template_returns_compact_first_guidance() -> None:
    prompts = await handle_list_prompts()

    assert len(prompts) == 1

    prompt = prompts[0]
    assert prompt.name == PROMPT_NAME
    assert prompt.title == "Use Web Evidence Fetch"
    assert [argument.name for argument in prompt.arguments or []] == ["url", "question"]

    prompt_result = await handle_get_prompt(
        PROMPT_NAME,
        {
            "url": "https://example.com/article",
            "question": "What happened?",
        },
    )

    assert prompt_result.description == "Compact-first usage guidance for kautuka_web_fetch."
    assert len(prompt_result.messages) == 1

    message = prompt_result.messages[0]
    assert message.role == "user"
    assert message.content.type == "text"
    assert "kautuka_web_fetch" in message.content.text
    assert "selectedChunks" in message.content.text
    assert "responseMode='full'" in message.content.text
    assert "https://example.com/article" in message.content.text
    assert "What happened?" in message.content.text


@pytest.mark.asyncio
async def test_prompt_template_requires_url_argument() -> None:
    with pytest.raises(ValueError, match="Prompt argument 'url' is required"):
        await handle_get_prompt(PROMPT_NAME, {"question": "What happened?"})
