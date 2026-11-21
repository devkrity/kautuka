from __future__ import annotations

from pathlib import Path

import pytest
from jsonschema import Draft202012Validator

from kautuka_web_tools.pipeline import FetchResult, build_payload, normalize_options
from kautuka_web_tools.server import (
    PROMPT_NAME,
    SERVER_INSTRUCTIONS,
    TOOL_NAME,
    handle_get_prompt,
    handle_list_prompts,
    handle_list_tools,
)


@pytest.mark.asyncio
@pytest.mark.parametrize("response_mode", ["compact", "full"])
@pytest.mark.parametrize("content_type", ["text/html", "text/plain"])
async def test_fetch_payload_matches_published_schema(response_mode: str, content_type: str) -> None:
    body = (
        (Path(__file__).parent / "fixtures" / "sample_article.html").read_bytes()
        if content_type == "text/html"
        else b"Example article. This is a plain text document."
    )
    url = "https://example.com/article"
    fetch_result = FetchResult(
        url=url,
        final_url=url,
        status_code=200,
        content_type=content_type,
        fetched_at_utc="2026-05-22T00:00:00Z",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(body),
        body=body,
        html_metadata={"title": "Example", "description": None, "byline": None},
    )
    options = normalize_options({"responseMode": response_mode})
    tool, = await handle_list_tools()

    assert tool.outputSchema is not None
    Draft202012Validator(tool.outputSchema).validate(build_payload(url, fetch_result, options))


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
