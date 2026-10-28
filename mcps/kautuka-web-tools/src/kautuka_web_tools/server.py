from __future__ import annotations

import asyncio
from typing import Any

import mcp.server.stdio
import mcp.types as types
from mcp.server.lowlevel import NotificationOptions, Server
from mcp.server.models import InitializationOptions

from kautuka_web_tools.pipeline import FetchError, LimitError, PipelineError, ValidationError, fetch_and_prepare, summarize_payload


SERVER_ID = "kautuka-web-tools"
SERVER_NAME = "Kautuka Web Tools"
SERVER_VERSION = "0.1.0"
TOOL_NAME = "kautuka_web_fetch"
PROMPT_NAME = "kautuka_web_fetch_usage"
SERVER_INSTRUCTIONS = (
    "Use kautuka_web_fetch to retrieve web evidence. Start with the compact response and use selectedChunks as the "
    "primary evidence surface. Escalate to responseMode='full' only when the full chunk or section index is needed. "
    "If structuredContent.guidance.followUpCalls is present, those arguments are the preferred way to continue with "
    "later chunk windows or raw markdown windows."
)
STRING_OR_NULL_SCHEMA = {"type": ["string", "null"]}
FOLLOW_UP_ARGUMENTS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "url": {"type": "string"},
        "query": {"type": "string"},
        "includeRawMarkdown": {"type": "boolean"},
        "responseMode": {"type": "string", "enum": ["compact", "full"]},
        "normalizationMode": {"type": "string", "enum": ["dom", "trafilatura_recall"]},
        "maxItems": {"type": "integer"},
        "maxChunkCount": {"type": "integer"},
        "topK": {"type": "integer"},
        "maxChunkChars": {"type": "integer"},
        "chunkOverlapChars": {"type": "integer"},
        "timeoutSeconds": {"type": "integer"},
        "fetchMode": {"type": "string", "enum": ["browser_http", "http"]},
        "expansionBudget": {"type": "integer"},
        "markdownOffsetChars": {"type": "integer"},
        "chunkOffset": {"type": "integer"},
    },
    "required": ["url"],
    "additionalProperties": False,
}
ITEM_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "itemId": {"type": "string"},
        "title": {"type": "string"},
        "url": {"type": "string"},
        "source": STRING_OR_NULL_SCHEMA,
        "publishedText": STRING_OR_NULL_SCHEMA,
        "byline": STRING_OR_NULL_SCHEMA,
        "summary": STRING_OR_NULL_SCHEMA,
        "sectionPath": {"type": "array", "items": {"type": "string"}},
        "sectionId": {"type": "string"},
    },
    "required": ["itemId", "title", "url", "sectionPath", "sectionId"],
    "additionalProperties": False,
}
SELECTED_CHUNK_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "rank": {"type": "integer"},
        "rootChunkId": {"type": "string"},
        "score": {"type": "number"},
        "reason": {"type": "string"},
        "expandedChunkIds": {"type": "array", "items": {"type": "string"}},
        "content": {"type": "string"},
        "contentCharCount": {"type": "integer"},
        "sectionPath": {"type": "array", "items": {"type": "string"}},
        "expansion": {
            "type": "object",
            "properties": {
                "budget": {"type": "integer"},
                "includedBefore": {"type": "integer"},
                "includedAfter": {"type": "integer"},
            },
            "required": ["budget", "includedBefore", "includedAfter"],
            "additionalProperties": False,
        },
    },
    "required": [
        "rank",
        "rootChunkId",
        "score",
        "reason",
        "expandedChunkIds",
        "content",
        "contentCharCount",
        "sectionPath",
        "expansion",
    ],
    "additionalProperties": False,
}
SECTION_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "sectionId": {"type": "string"},
        "heading": STRING_OR_NULL_SCHEMA,
        "level": {"type": "integer"},
        "sectionPath": {"type": "array", "items": {"type": "string"}},
        "startLine": {"type": "integer"},
        "endLine": {"type": "integer"},
        "charStart": {"type": "integer"},
        "charEnd": {"type": "integer"},
    },
    "required": ["sectionId", "heading", "level", "sectionPath", "startLine", "endLine", "charStart", "charEnd"],
    "additionalProperties": False,
}
CHUNK_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "chunkId": {"type": "string"},
        "ordinal": {"type": "integer"},
        "sectionId": {"type": "string"},
        "sectionPath": {"type": "array", "items": {"type": "string"}},
        "heading": STRING_OR_NULL_SCHEMA,
        "sourceCharStart": {"type": "integer"},
        "sourceCharEnd": {"type": "integer"},
        "characterCount": {"type": "integer"},
        "advisoryTokenEstimate": {"type": "integer"},
        "overlapPreviousChars": {"type": "integer"},
        "overlapNextChars": {"type": "integer"},
        "text": {"type": "string"},
        "provenance": {"type": "object", "additionalProperties": True},
    },
    "required": [
        "chunkId",
        "ordinal",
        "sectionId",
        "sectionPath",
        "heading",
        "sourceCharStart",
        "sourceCharEnd",
        "characterCount",
        "advisoryTokenEstimate",
        "overlapPreviousChars",
        "overlapNextChars",
        "text",
        "provenance",
    ],
    "additionalProperties": False,
}
ITEM_WINDOW_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "returnedCount": {"type": "integer"},
        "totalCount": {"type": "integer"},
        "truncated": {"type": "boolean"},
        "nextOptions": {
            "anyOf": [
                FOLLOW_UP_ARGUMENTS_SCHEMA,
                {"type": "null"},
            ]
        },
    },
    "required": ["returnedCount", "totalCount", "truncated", "nextOptions"],
    "additionalProperties": False,
}
PAGING_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "truncated": {"type": "boolean"},
        "truncationReasons": {"type": "array", "items": {"type": "string"}},
        "window": {
            "type": "object",
            "properties": {
                "offsetChars": {"type": "integer"},
                "returnedChars": {"type": "integer"},
                "totalChars": {"type": "integer"},
                "hasMoreBefore": {"type": "boolean"},
                "hasMoreAfter": {"type": "boolean"},
            },
            "required": ["offsetChars", "returnedChars", "totalChars", "hasMoreBefore", "hasMoreAfter"],
            "additionalProperties": False,
        },
        "nextOptions": {
            "anyOf": [
                FOLLOW_UP_ARGUMENTS_SCHEMA,
                {"type": "null"},
            ]
        },
    },
    "required": ["truncated", "truncationReasons", "window", "nextOptions"],
    "additionalProperties": False,
}
CHUNK_WINDOW_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "truncated": {"type": "boolean"},
        "truncationReasons": {"type": "array", "items": {"type": "string"}},
        "window": {
            "type": "object",
            "properties": {
                "offset": {"type": "integer"},
                "returnedCount": {"type": "integer"},
                "totalCount": {"type": "integer"},
                "hasMoreBefore": {"type": "boolean"},
                "hasMoreAfter": {"type": "boolean"},
            },
            "required": ["offset", "returnedCount", "totalCount", "hasMoreBefore", "hasMoreAfter"],
            "additionalProperties": False,
        },
        "nextOptions": {
            "anyOf": [
                FOLLOW_UP_ARGUMENTS_SCHEMA,
                {"type": "null"},
            ]
        },
    },
    "required": ["truncated", "truncationReasons", "window", "nextOptions"],
    "additionalProperties": False,
}
LIMITS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "responseMode": {"type": "string", "enum": ["compact", "full"]},
        "maxResponseBytes": {"type": "integer"},
        "maxNormalizedMarkdownChars": {"type": "integer"},
        "maxChunkCount": {"type": "integer"},
        "maxChunkChars": {"type": "integer"},
        "chunkOverlapChars": {"type": "integer"},
        "markdownOffsetChars": {"type": "integer"},
        "chunkOffset": {"type": "integer"},
        "maxItems": {"type": "integer"},
        "redirectCap": {"type": "integer"},
        "timeoutSeconds": {"type": "integer"},
        "allowedContentTypes": {"type": "array", "items": {"type": "string"}},
        "sizingMetric": {"type": "string", "enum": ["characters"]},
    },
    "required": [
        "responseMode",
        "maxResponseBytes",
        "maxNormalizedMarkdownChars",
        "maxChunkCount",
        "maxChunkChars",
        "chunkOverlapChars",
        "markdownOffsetChars",
        "chunkOffset",
        "maxItems",
        "redirectCap",
        "timeoutSeconds",
        "allowedContentTypes",
        "sizingMetric",
    ],
    "additionalProperties": False,
}
CONTENT_INDEX_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "responseMode": {"type": "string", "enum": ["compact"]},
        "fullResponseMode": {"type": "string", "enum": ["full"]},
        "sectionsIncluded": {"type": "boolean", "enum": [False]},
        "chunksIncluded": {"type": "boolean", "enum": [False]},
        "itemsIncluded": {"type": "boolean"},
    },
    "required": ["responseMode", "fullResponseMode", "sectionsIncluded", "chunksIncluded", "itemsIncluded"],
    "additionalProperties": False,
}
TOOL_OUTPUT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "fetch": {
            "type": "object",
            "properties": {
                "requestedUrl": {"type": "string"},
                "finalUrl": {"type": "string"},
                "statusCode": {"type": "integer"},
                "contentType": {"type": "string"},
                "fetchedAtUtc": {"type": "string"},
                "fetchStrategy": {"type": "string"},
                "redirectCount": {"type": "integer"},
                "responseBytes": {"type": "integer"},
                "responseBytesLimitExceeded": {"type": "boolean"},
            },
            "required": [
                "requestedUrl",
                "finalUrl",
                "statusCode",
                "contentType",
                "fetchedAtUtc",
                "fetchStrategy",
                "redirectCount",
                "responseBytes",
                "responseBytesLimitExceeded",
            ],
            "additionalProperties": False,
        },
        "normalization": {
            "type": "object",
            "properties": {
                "normalizer": {"type": "string"},
                "normalizerVersion": {"type": "string"},
                "preCleaner": STRING_OR_NULL_SCHEMA,
                "preCleanerApplied": {"type": "boolean"},
                "preCleanerRejectedReason": STRING_OR_NULL_SCHEMA,
                "normalizationMode": {"type": "string"},
                "contentPostProcessor": STRING_OR_NULL_SCHEMA,
                "contentPostProcessorApplied": {"type": "boolean"},
            },
            "required": [
                "normalizer",
                "normalizerVersion",
                "preCleaner",
                "preCleanerApplied",
                "preCleanerRejectedReason",
                "normalizationMode",
                "contentPostProcessor",
                "contentPostProcessorApplied",
            ],
            "additionalProperties": False,
        },
        "document": {
            "type": "object",
            "properties": {
                "title": STRING_OR_NULL_SCHEMA,
                "description": STRING_OR_NULL_SCHEMA,
                "byline": STRING_OR_NULL_SCHEMA,
                "topLevelHeadings": {"type": "array", "items": {"type": "string"}},
                "sectionCount": {"type": "integer"},
                "chunkCount": {"type": "integer"},
                "itemCount": {"type": "integer"},
            },
            "required": ["title", "description", "byline", "topLevelHeadings", "sectionCount", "chunkCount", "itemCount"],
            "additionalProperties": False,
        },
        "guidance": {
            "type": "object",
            "properties": {
                "primarySurface": {"type": "string", "enum": ["selectedChunks"]},
                "whenToAskForMore": {"type": "array", "items": {"type": "string"}},
                "followUpCalls": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "purpose": {"type": "string"},
                            "arguments": FOLLOW_UP_ARGUMENTS_SCHEMA,
                        },
                        "required": ["purpose", "arguments"],
                        "additionalProperties": False,
                    },
                },
            },
            "required": ["primarySurface", "whenToAskForMore", "followUpCalls"],
            "additionalProperties": False,
        },
        "items": {"type": "array", "items": ITEM_SCHEMA},
        "itemWindow": ITEM_WINDOW_SCHEMA,
        "paging": PAGING_SCHEMA,
        "chunkWindow": CHUNK_WINDOW_SCHEMA,
        "limits": LIMITS_SCHEMA,
        "selectedChunks": {"type": "array", "items": SELECTED_CHUNK_SCHEMA},
        "warnings": {"type": "array", "items": {"type": "string"}},
        "rawMarkdown": STRING_OR_NULL_SCHEMA,
        "contentIndex": CONTENT_INDEX_SCHEMA,
        "sections": {"type": "array", "items": SECTION_SCHEMA},
        "chunks": {"type": "array", "items": CHUNK_SCHEMA},
    },
    "required": [
        "fetch",
        "normalization",
        "document",
        "guidance",
        "items",
        "itemWindow",
        "paging",
        "chunkWindow",
        "limits",
        "selectedChunks",
        "warnings",
        "rawMarkdown",
    ],
    "additionalProperties": False,
}

server = Server(SERVER_ID)


@server.list_prompts()
async def handle_list_prompts() -> list[types.Prompt]:
    return [
        types.Prompt(
            name=PROMPT_NAME,
            title="Use Web Evidence Fetch",
            description=(
                "Guidance template for using kautuka_web_fetch with a compact-first call pattern and standard "
                "follow-up handling."
            ),
            arguments=[
                types.PromptArgument(
                    name="url",
                    description="The HTTP or HTTPS URL to inspect with kautuka_web_fetch.",
                    required=True,
                ),
                types.PromptArgument(
                    name="question",
                    description="Optional question or retrieval goal to drive the initial query.",
                    required=False,
                ),
            ],
        )
    ]


@server.get_prompt()
async def handle_get_prompt(name: str, arguments: dict[str, str] | None) -> types.GetPromptResult:
    if name != PROMPT_NAME:
        raise ValueError(f"Unknown prompt: {name}")

    prompt_arguments = arguments or {}
    url = (prompt_arguments.get("url") or "").strip()
    if not url:
        raise ValueError("Prompt argument 'url' is required.")

    question = (prompt_arguments.get("question") or "").strip()
    initial_arguments = {
        "url": url,
        "responseMode": "compact",
    }
    if question:
        initial_arguments["query"] = question

    guidance_lines = [
        "Use the kautuka_web_fetch tool to retrieve evidence from the supplied URL.",
        "Start with a compact call and treat selectedChunks as the primary evidence surface.",
        "Only escalate to responseMode='full' when you need the full chunk or section index.",
        "If structuredContent.guidance.followUpCalls is present, prefer those arguments for continuation.",
        "If paging.nextOptions is present and the needed text may be outside the current markdown window, request that next window with includeRawMarkdown=true.",
        "Summarize or answer strictly from the returned evidence and note if the result is partial.",
        "",
        "Initial tool arguments:",
        str(initial_arguments),
    ]

    if question:
        guidance_lines.extend([
            "",
            f"Question or retrieval goal: {question}",
        ])

    return types.GetPromptResult(
        description="Compact-first usage guidance for kautuka_web_fetch.",
        messages=[
            types.PromptMessage(
                role="user",
                content=types.TextContent(type="text", text="\n".join(guidance_lines)),
            )
        ],
    )


@server.list_tools()
async def handle_list_tools() -> list[types.Tool]:
    return [
        types.Tool(
            name=TOOL_NAME,
            title="Web Evidence Fetch",
            description=(
                "Fetch a web page over plain HTTP(S), normalize it to Markdown, parse sections, "
                "chunk content by structure-aware boundaries, and return compact evidence by default "
                "with an optional full response mode. Start with selectedChunks. Ask for responseMode='full' "
                "only when you need the full chunk or section index. If chunkWindow.nextOptions or paging.nextOptions "
                "is present, call the tool again with those arguments to continue."
            ),
            annotations=types.ToolAnnotations(
                title="Web Evidence Fetch",
                readOnlyHint=True,
                destructiveHint=False,
                idempotentHint=True,
                openWorldHint=True,
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "url": {
                        "type": "string",
                        "description": "HTTP or HTTPS URL to fetch.",
                    },
                    "query": {
                        "type": "string",
                        "description": "Optional lexical query used to derive selectedChunks.",
                    },
                    "includeRawMarkdown": {
                        "type": "boolean",
                        "description": "When true, include the bounded normalized markdown window in the response.",
                    },
                    "responseMode": {
                        "type": "string",
                        "enum": ["compact", "full"],
                        "description": "compact returns a smaller evidence-first payload by default; full also includes sections and chunks.",
                    },
                    "normalizationMode": {
                        "type": "string",
                        "enum": ["dom", "trafilatura_recall"],
                        "description": "dom is the default preserve-first DOM cleanup path; trafilatura_recall is an explicit alternate recall-biased normalization mode.",
                    },
                    "maxItems": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 200,
                        "description": "Maximum number of returned items in compact mode. full mode returns all items.",
                    },
                    "maxChunkCount": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 512,
                        "description": "Maximum number of chunks to return in the current chunk window. Used mainly with responseMode='full'.",
                    },
                    "topK": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 10,
                        "description": "Maximum selected evidence windows to return.",
                    },
                    "maxChunkChars": {
                        "type": "integer",
                        "minimum": 400,
                        "maximum": 6000,
                        "description": "Character-based hard size limit per chunk in v1.",
                    },
                    "chunkOverlapChars": {
                        "type": "integer",
                        "minimum": 0,
                        "maximum": 800,
                        "description": "Character overlap budget between adjacent chunks.",
                    },
                    "timeoutSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 60,
                        "description": "HTTP timeout in seconds.",
                    },
                    "userAgent": {
                        "type": "string",
                        "description": "Optional user agent override for the fetch request.",
                    },
                    "fetchMode": {
                        "type": "string",
                        "enum": ["browser_http", "http"],
                        "description": "Fetch strategy. browser_http is the default browser-like HTTP profile; http remains a compatibility alias.",
                    },
                    "expansionBudget": {
                        "type": "integer",
                        "minimum": 0,
                        "maximum": 6,
                        "description": "Maximum number of neighbor chunks to add after ranking.",
                    },
                    "markdownOffsetChars": {
                        "type": "integer",
                        "minimum": 0,
                        "description": "Optional character offset into normalized markdown for bounded paging when raw markdown is requested.",
                    },
                    "chunkOffset": {
                        "type": "integer",
                        "minimum": 0,
                        "description": "Optional chunk offset for retrieving the next full-mode chunk window.",
                    },
                },
                "required": ["url"],
                "additionalProperties": False,
            },
            outputSchema=TOOL_OUTPUT_SCHEMA,
        )
    ]


@server.call_tool()
async def handle_call_tool(name: str, arguments: dict[str, Any]) -> types.CallToolResult:
    if name != TOOL_NAME:
        raise ValueError(f"Unknown tool: {name}")

    url = arguments.get("url")
    if not isinstance(url, str) or not url.strip():
        return error_result("url is required and must be a non-empty string.")

    try:
        payload = await fetch_and_prepare(url.strip(), arguments)
    except ValidationError as exc:
        return error_result(str(exc))
    except LimitError as exc:
        return error_result(str(exc))
    except FetchError as exc:
        return error_result(str(exc))
    except PipelineError as exc:
        return error_result(str(exc))

    return types.CallToolResult(
        content=[types.TextContent(type="text", text=summarize_payload(payload))],
        structuredContent=payload,
    )


def error_result(message: str) -> types.CallToolResult:
    return types.CallToolResult(
        isError=True,
        content=[types.TextContent(type="text", text=message)],
        structuredContent={
            "error": {
                "message": message,
            }
        },
    )


async def run() -> None:
    async with mcp.server.stdio.stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream,
            write_stream,
            InitializationOptions(
                server_name=SERVER_NAME,
                server_version=SERVER_VERSION,
                capabilities=server.get_capabilities(
                    notification_options=NotificationOptions(),
                    experimental_capabilities={},
                ),
                instructions=SERVER_INSTRUCTIONS,
            ),
        )


def main() -> None:
    asyncio.run(run())


if __name__ == "__main__":
    main()
