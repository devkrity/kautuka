from __future__ import annotations

from io import BytesIO
import ipaddress
import re
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from html.parser import HTMLParser
from typing import Any
from urllib.parse import urljoin, urlparse

import httpx
from bs4 import BeautifulSoup, Tag
from markitdown import MarkItDown
import trafilatura


DEFAULT_TIMEOUT_SECONDS = 15
DEFAULT_FETCH_MODE = "browser_http"
DEFAULT_RESPONSE_MODE = "compact"
DEFAULT_NORMALIZATION_MODE = "dom"
FETCH_MODE_ALIASES = {
    "http": DEFAULT_FETCH_MODE,
    DEFAULT_FETCH_MODE: DEFAULT_FETCH_MODE,
}
RESPONSE_MODE_ALIASES = {
    DEFAULT_RESPONSE_MODE: DEFAULT_RESPONSE_MODE,
    "full": "full",
}
NORMALIZATION_MODE_ALIASES = {
    DEFAULT_NORMALIZATION_MODE: DEFAULT_NORMALIZATION_MODE,
    "trafilatura_recall": "trafilatura_recall",
}
DEFAULT_TOP_K = 3
MAX_TOP_K = 10
DEFAULT_MAX_ITEMS = 40
MAX_MAX_ITEMS = 200
DEFAULT_COMPACT_MAX_ITEMS = 8
DEFAULT_CHUNK_OFFSET = 0
DEFAULT_MAX_CHUNK_CHARS = 1800
MAX_MAX_CHUNK_CHARS = 6000
DEFAULT_CHUNK_OVERLAP_CHARS = 180
MAX_CHUNK_OVERLAP_CHARS = 800
DEFAULT_EXPANSION_BUDGET = 2
MAX_EXPANSION_BUDGET = 6
DEFAULT_COMPACT_EXPANSION_BUDGET = 1
DEFAULT_COMPACT_SELECTED_CHUNK_CHARS = 900
DEFAULT_COMPACT_ITEM_SUMMARY_CHARS = 240
DEFAULT_MAX_RESPONSE_BYTES = 1_000_000
DEFAULT_MAX_NORMALIZED_MARKDOWN_CHARS = 120_000
DEFAULT_MAX_CHUNK_COUNT = 128
DEFAULT_REDIRECT_CAP = 5
MIN_MARKDOWN_CHARS = 200
MIN_STRIPPED_MARKDOWN_RATIO = 0.2
MIN_PRIMARY_CONTAINER_TEXT_CHARS = 280
MAX_LEADING_NOISE_BLOCKS = 12
ALLOWED_CONTENT_TYPES = (
    "text/html",
    "application/xhtml+xml",
    "text/plain",
    "text/markdown",
)
TOKEN_PATTERN = re.compile(r"[a-z0-9]{2,}")
HEADING_PATTERN = re.compile(r"^(#{1,6})\s+(.*\S)\s*$")
SENTENCE_BOUNDARY_PATTERN = re.compile(r"(?<=[.!?])\s+")
WHITESPACE_PATTERN = re.compile(r"\s+")
MARKDOWN_LINK_PATTERN = re.compile(r"\[(?P<title>[^\]]+)\]\((?P<url>[^)]+)\)")
TIME_LIKE_PATTERN = re.compile(
    r"\b(?:\d+\s+(?:minute|hour|day|week|month|year)s?\s+ago|yesterday|today|just now)\b",
    re.IGNORECASE,
)
STOP_WORDS = {
    "a",
    "an",
    "and",
    "are",
    "as",
    "at",
    "be",
    "by",
    "for",
    "from",
    "in",
    "is",
    "it",
    "of",
    "on",
    "or",
    "that",
    "the",
    "to",
    "with",
}
SKIP_TITLE_PREFIXES = (
    "see more",
    "more headlines",
    "sign in",
    "help",
    "settings",
    "google weather",
)
GENERIC_CTA_PREFIXES = (
    "learn more",
    "read more",
    "read the story",
    "shop now",
    "watch now",
    "discover more",
    "explore more",
)
SKIP_BLOCK_VALUES = {
    "more",
    "see more",
    "sign in",
    "---",
}
IMAGE_URL_HINTS = (
    "/is/image/",
    "/image/upload/",
)
IMAGE_URL_SUFFIXES = (
    ".png",
    ".jpg",
    ".jpeg",
    ".gif",
    ".webp",
    ".svg",
    ".avif",
)
IMAGE_QUERY_FORMAT_HINTS = (
    "fmt=png",
    "fmt=png-alpha",
    "fmt=jpg",
    "fmt=jpeg",
    "fmt=webp",
    "format=png",
    "format=jpg",
    "format=jpeg",
    "format=webp",
)
NOISE_BLOCK_PATTERNS = (
    "trace id is missing",
    "your privacy choices",
    "manage cookies",
    "back to top",
    "quick links",
    "advertisement - scroll to continue",
)
PROMOTIONAL_DISCLAIMER_PREFIXES = (
    "advertisement",
    "advertiser content",
    "sponsored",
    "sponsored content",
    "partner content",
    "paid content",
    "paid post",
    "promoted",
)
PROMOTIONAL_CTA_PHRASES = (
    "subscribe",
    "sign up",
    "newsletter",
    "buy now",
    "shop now",
    "download the app",
    "install the app",
    "start free trial",
    "free trial",
    "special offer",
    "limited time",
)
RELATED_SECTION_PATTERNS = (
    "utm_campaign=related-section",
    "utm_medium=spotlight-page",
    "additional links",
)
FOOTER_NAVIGATION_TERMS = (
    "about us",
    "advertise",
    "partner content",
    "archives",
    "follow us on",
    "privacy policy",
    "terms & conditions",
    "disclaimer",
    "feedback",
    "investors",
    "news updates",
    "more links",
    "group sites",
    "code of ethics",
)
DISCOUNT_PATTERN = re.compile(r"\b(?:\d+%\s+off|save\s+\d+%|special offer|limited time)\b", re.IGNORECASE)
HTML_ALWAYS_DROP_TAGS = {
    "script",
    "style",
    "svg",
    "noscript",
    "iframe",
    "canvas",
    "form",
    "button",
    "input",
    "textarea",
    "select",
    "option",
    "template",
}
HTML_STRUCTURAL_DROP_TAGS = {
    "nav",
    "aside",
    "footer",
}
HTML_DROP_CONTAINER_TAGS = {
    "header",
    "ul",
    "ol",
    "menu",
    "dialog",
    "details",
    "aside",
    "nav",
    "footer",
}
HTML_CANDIDATE_TAGS = {
    "main",
    "article",
    "section",
    "div",
    "body",
}
HTML_BOILERPLATE_TERMS = {
    "nav",
    "menu",
    "breadcrumb",
    "cookie",
    "consent",
    "banner",
    "share",
    "social",
    "subscribe",
    "newsletter",
    "signin",
    "login",
    "search",
    "advert",
    "ads",
    "promo",
    "sponsored",
    "partner",
    "paid",
    "recommend",
    "recommendation",
    "modal",
    "popup",
    "drawer",
    "toolbar",
    "utility",
}
HTML_PRESERVE_TERMS = {
    "main",
    "article",
    "content",
    "story",
    "post",
    "entry",
    "body",
}
HTML_BOILERPLATE_ROLES = {
    "navigation",
    "search",
    "banner",
    "contentinfo",
    "complementary",
    "dialog",
}
HTML_ATTR_TOKEN_PATTERN = re.compile(r"[a-z0-9]+")
MULTI_QUOTE_RUN_PATTERN = re.compile(r"(?:double quotation mark\s*){2,}", re.IGNORECASE)
TITLE_NOISE_SUFFIXES = (
    "your privacy choices opt-out icon",
    "your privacy choices",
)
ITEM_TITLE_SUFFIX_PATTERNS = (
    re.compile(r"\s+chevron\\?_right$", re.IGNORECASE),
)
ITEM_TITLE_PREFIX_PATTERNS = (
    re.compile(r"^(?:[$£€¥]\s*)?(?:[•·●]\s*){2,}on\s+", re.IGNORECASE),
)


class PipelineError(Exception):
    """Base error raised for web retrieval failures."""


class ValidationError(PipelineError):
    """Raised when request input is invalid."""


class FetchError(PipelineError):
    """Raised when HTTP fetch fails."""


class LimitError(PipelineError):
    """Raised when a hard limit is exceeded."""


@dataclass(slots=True)
class FetchOptions:
    query: str | None = None
    include_raw_markdown: bool = False
    response_mode: str = DEFAULT_RESPONSE_MODE
    normalization_mode: str = DEFAULT_NORMALIZATION_MODE
    top_k: int = DEFAULT_TOP_K
    max_items: int = DEFAULT_MAX_ITEMS
    chunk_offset: int = DEFAULT_CHUNK_OFFSET
    max_chunk_chars: int = DEFAULT_MAX_CHUNK_CHARS
    chunk_overlap_chars: int = DEFAULT_CHUNK_OVERLAP_CHARS
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS
    user_agent: str | None = None
    fetch_mode: str = DEFAULT_FETCH_MODE
    expansion_budget: int = DEFAULT_EXPANSION_BUDGET
    markdown_offset_chars: int = 0
    max_response_bytes: int = DEFAULT_MAX_RESPONSE_BYTES
    max_normalized_markdown_chars: int = DEFAULT_MAX_NORMALIZED_MARKDOWN_CHARS
    max_chunk_count: int = DEFAULT_MAX_CHUNK_COUNT
    redirect_cap: int = DEFAULT_REDIRECT_CAP
    allowed_content_types: tuple[str, ...] = ALLOWED_CONTENT_TYPES


@dataclass(slots=True)
class FetchResult:
    url: str
    final_url: str
    status_code: int
    content_type: str
    fetched_at_utc: str
    fetch_strategy: str
    redirect_count: int
    response_bytes: int
    body: bytes
    html_metadata: dict[str, str | None]
    response_bytes_limit_exceeded: bool = False


@dataclass(slots=True)
class Section:
    section_id: str
    heading: str | None
    level: int
    section_path: list[str]
    start_line: int
    end_line: int
    char_start: int
    char_end: int
    markdown: str


@dataclass(slots=True)
class Chunk:
    chunk_id: str
    ordinal: int
    section_id: str
    section_path: list[str]
    heading: str | None
    source_char_start: int
    source_char_end: int
    character_count: int
    advisory_token_estimate: int
    overlap_previous_chars: int
    overlap_next_chars: int
    text: str
    provenance: dict[str, Any]


def clamp(value: int, minimum: int, maximum: int) -> int:
    return max(minimum, min(maximum, int(value)))


def normalize_options(arguments: dict[str, Any]) -> FetchOptions:
    query = arguments.get("query")
    if query is not None and not isinstance(query, str):
        raise ValidationError("query must be a string when provided.")

    raw_fetch_mode = arguments.get("fetchMode", DEFAULT_FETCH_MODE)
    if not isinstance(raw_fetch_mode, str):
        raise ValidationError("fetchMode must be a string when provided.")

    fetch_mode = FETCH_MODE_ALIASES.get(raw_fetch_mode)
    if fetch_mode is None:
        raise ValidationError("Only fetchMode='browser_http' is supported in v1. 'http' remains an alias.")

    raw_response_mode = arguments.get("responseMode", DEFAULT_RESPONSE_MODE)
    if not isinstance(raw_response_mode, str):
        raise ValidationError("responseMode must be a string when provided.")

    response_mode = RESPONSE_MODE_ALIASES.get(raw_response_mode)
    if response_mode is None:
        raise ValidationError("responseMode must be either 'compact' or 'full'.")

    raw_normalization_mode = arguments.get("normalizationMode", DEFAULT_NORMALIZATION_MODE)
    if not isinstance(raw_normalization_mode, str):
        raise ValidationError("normalizationMode must be a string when provided.")

    normalization_mode = NORMALIZATION_MODE_ALIASES.get(raw_normalization_mode)
    if normalization_mode is None:
        raise ValidationError("normalizationMode must be either 'dom' or 'trafilatura_recall'.")

    user_agent = arguments.get("userAgent")
    if user_agent is not None and not isinstance(user_agent, str):
        raise ValidationError("userAgent must be a string when provided.")

    include_raw_markdown = arguments.get("includeRawMarkdown", False) is True

    return FetchOptions(
        query=query.strip() if isinstance(query, str) and query.strip() else None,
        include_raw_markdown=include_raw_markdown,
        response_mode=response_mode,
        normalization_mode=normalization_mode,
        top_k=clamp(arguments.get("topK", DEFAULT_TOP_K), 1, MAX_TOP_K),
        max_items=clamp(arguments.get("maxItems", DEFAULT_MAX_ITEMS), 1, MAX_MAX_ITEMS),
        chunk_offset=max(0, int(arguments.get("chunkOffset", DEFAULT_CHUNK_OFFSET))),
        max_chunk_count=clamp(arguments.get("maxChunkCount", DEFAULT_MAX_CHUNK_COUNT), 1, 512),
        max_chunk_chars=clamp(
            arguments.get("maxChunkChars", DEFAULT_MAX_CHUNK_CHARS),
            400,
            MAX_MAX_CHUNK_CHARS,
        ),
        chunk_overlap_chars=clamp(
            arguments.get("chunkOverlapChars", DEFAULT_CHUNK_OVERLAP_CHARS),
            0,
            MAX_CHUNK_OVERLAP_CHARS,
        ),
        timeout_seconds=clamp(arguments.get("timeoutSeconds", DEFAULT_TIMEOUT_SECONDS), 1, 60),
        user_agent=user_agent.strip() if isinstance(user_agent, str) and user_agent.strip() else None,
        fetch_mode=fetch_mode,
        expansion_budget=clamp(
            arguments.get("expansionBudget", DEFAULT_EXPANSION_BUDGET),
            0,
            MAX_EXPANSION_BUDGET,
        ),
        markdown_offset_chars=max(0, int(arguments.get("markdownOffsetChars", 0))),
    )


def validate_url(url: str) -> None:
    parsed = urlparse(url)
    if parsed.scheme not in {"http", "https"}:
        raise ValidationError("Only http and https URLs are supported.")

    if not parsed.netloc:
        raise ValidationError("URL must include a host.")

    host = parsed.hostname
    if host is None:
        raise ValidationError("URL host could not be determined.")

    if host.lower() == "localhost":
        raise ValidationError("localhost is not allowed.")

    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return

    if address.is_private or address.is_loopback or address.is_link_local or address.is_multicast:
        raise ValidationError("Private or loopback network targets are not allowed.")


def build_headers(options: FetchOptions) -> dict[str, str]:
    user_agent = options.user_agent or (
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36"
    )
    return {
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
        "Accept-Language": "en-US,en;q=0.9",
        "Cache-Control": "no-cache",
        "Pragma": "no-cache",
        "Upgrade-Insecure-Requests": "1",
        "Sec-Fetch-Site": "none",
        "Sec-Fetch-Mode": "navigate",
        "Sec-Fetch-User": "?1",
        "Sec-Fetch-Dest": "document",
        "User-Agent": user_agent,
    }


async def fetch_url(url: str, options: FetchOptions) -> FetchResult:
    validate_url(url)

    timeout = httpx.Timeout(options.timeout_seconds)
    limits = httpx.Limits(max_keepalive_connections=5, max_connections=10)

    async with httpx.AsyncClient(
        headers=build_headers(options),
        follow_redirects=True,
        timeout=timeout,
        limits=limits,
        http2=True,
        max_redirects=options.redirect_cap,
    ) as client:
        try:
            response = await client.get(url)
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise FetchError(f"Timed out fetching {url}.") from exc
        except httpx.HTTPStatusError as exc:
            raise FetchError(
                f"HTTP fetch failed with status {exc.response.status_code} for {url}."
            ) from exc
        except httpx.HTTPError as exc:
            raise FetchError(f"Failed to fetch {url}: {exc}") from exc

    content_type = response.headers.get("content-type", "application/octet-stream").split(";", 1)[0].strip().lower()
    if not is_allowed_content_type(content_type, options.allowed_content_types):
        raise ValidationError(f"Unsupported content type: {content_type}")

    body = response.content
    response_bytes = len(body)
    response_bytes_limit_exceeded = response_bytes > options.max_response_bytes

    parsed_html_metadata = extract_html_metadata(body.decode(response.encoding or "utf-8", errors="ignore"))

    return FetchResult(
        url=url,
        final_url=str(response.url),
        status_code=response.status_code,
        content_type=content_type,
        fetched_at_utc=datetime.now(UTC).isoformat(),
        fetch_strategy="http",
        redirect_count=len(response.history),
        response_bytes=response_bytes,
        body=body,
        html_metadata=parsed_html_metadata,
        response_bytes_limit_exceeded=response_bytes_limit_exceeded,
    )


def is_allowed_content_type(content_type: str, allowed_types: tuple[str, ...]) -> bool:
    return any(
        content_type == allowed or (allowed.endswith("/*") and content_type.startswith(allowed[:-1]))
        for allowed in allowed_types
    )


def normalize_markdown(fetch_result: FetchResult, options: FetchOptions) -> tuple[str, dict[str, Any]]:
    if fetch_result.content_type in {"text/plain", "text/markdown"}:
        markdown = fetch_result.body.decode("utf-8", errors="ignore")
        cleaned_markdown, filter_stats = clean_markdown_for_retrieval(markdown, remove_leading_noise=False)
        metadata = {
            "normalizer": "identity-text",
            "normalizerVersion": "1.0",
            "normalizationMode": options.normalization_mode,
        }
    else:
        raw_markdown = convert_html_to_markdown(
            fetch_result.body,
            source_url=fetch_result.final_url,
        )
        markdown = raw_markdown
        normalization_details = normalize_html_content(fetch_result.body, fetch_result.final_url, raw_markdown, options)
        if normalization_details["markdown"]:
            markdown = normalization_details["markdown"]

        metadata = {
            "normalizer": normalization_details["normalizer"],
            "normalizerVersion": "0.1",
            "preCleaner": normalization_details["preCleaner"],
            "preCleanerApplied": normalization_details["preCleanerApplied"],
            "metadataExtractor": "html-meta-parser",
            "preCleanerRejectedReason": normalization_details["preCleanerRejectedReason"],
            "preCleanerRemovedElements": normalization_details["preCleanerRemovedElements"],
            "normalizationMode": options.normalization_mode,
            "contentPostProcessor": "markdown-noise-filter",
        }
        cleaned_markdown, filter_stats = clean_markdown_for_retrieval(markdown)

    if should_use_cleaned_markdown(cleaned_markdown, markdown, filter_stats["removedBlockCount"]):
        markdown = cleaned_markdown

    metadata["contentPostProcessor"] = "markdown-noise-filter"
    metadata["contentPostProcessorApplied"] = filter_stats["removedBlockCount"] > 0
    metadata["contentPostProcessorRemovedBlocks"] = filter_stats["removedBlockCount"]

    return markdown, metadata


def normalize_html_content(
    content: bytes,
    source_url: str,
    raw_markdown: str,
    options: FetchOptions,
) -> dict[str, Any]:
    if options.normalization_mode == "trafilatura_recall":
        recall_markdown = convert_html_with_trafilatura_recall(content, source_url)
        if should_use_stripped_markdown(recall_markdown, raw_markdown):
            return {
                "markdown": recall_markdown,
                "normalizer": "trafilatura_recall",
                "preCleaner": "trafilatura",
                "preCleanerApplied": True,
                "preCleanerRejectedReason": None,
                "preCleanerRemovedElements": 0,
            }
        return {
            "markdown": raw_markdown,
            "normalizer": "markitdown",
            "preCleaner": "trafilatura",
            "preCleanerApplied": False,
            "preCleanerRejectedReason": "trafilatura-markdown-too-thin",
            "preCleanerRemovedElements": 0,
        }

    stripped_body, strip_stats = strip_html_boilerplate(content)
    if strip_stats["removedElementCount"] <= 0:
        return {
            "markdown": raw_markdown,
            "normalizer": "markitdown",
            "preCleaner": "dom-boilerplate-stripper",
            "preCleanerApplied": False,
            "preCleanerRejectedReason": None,
            "preCleanerRemovedElements": 0,
        }

    stripped_markdown = convert_html_to_markdown(
        stripped_body,
        source_url=source_url,
    )
    if should_use_stripped_markdown(stripped_markdown, raw_markdown):
        return {
            "markdown": stripped_markdown,
            "normalizer": "markitdown",
            "preCleaner": "dom-boilerplate-stripper",
            "preCleanerApplied": True,
            "preCleanerRejectedReason": None,
            "preCleanerRemovedElements": strip_stats["removedElementCount"],
        }

    return {
        "markdown": raw_markdown,
        "normalizer": "markitdown",
        "preCleaner": "dom-boilerplate-stripper",
        "preCleanerApplied": False,
        "preCleanerRejectedReason": "stripped-markdown-too-thin",
        "preCleanerRemovedElements": strip_stats["removedElementCount"],
    }


def clean_markdown_for_retrieval(markdown: str, *, remove_leading_noise: bool = True) -> tuple[str, dict[str, int]]:
    blocks = split_markdown_blocks(markdown)
    if not blocks:
        return markdown, {"removedBlockCount": 0}

    removed_count = 0
    start_index = 0
    if remove_leading_noise:
        while start_index < min(len(blocks), MAX_LEADING_NOISE_BLOCKS):
            block = blocks[start_index]
            if not is_probable_leading_noise_block(block):
                break
            removed_count += 1
            start_index += 1

    filtered_blocks: list[str] = []
    seen_signatures: set[str] = set()
    for index, block in enumerate(blocks[start_index:], start=start_index):
        if is_probable_global_noise_block(block):
            removed_count += 1
            continue
        block_signature = build_block_signature(block)
        if block_signature in seen_signatures:
            removed_count += 1
            continue
        seen_signatures.add(block_signature)
        filtered_blocks.append(block)

    cleaned_markdown = "\n\n".join(filtered_blocks).strip()
    return cleaned_markdown or markdown, {"removedBlockCount": removed_count}


def should_use_cleaned_markdown(cleaned_markdown: str, original_markdown: str, removed_block_count: int) -> bool:
    if not cleaned_markdown.strip():
        return False
    if len(cleaned_markdown) >= MIN_MARKDOWN_CHARS:
        return True
    if removed_block_count <= 0:
        return False
    if not original_markdown.strip():
        return True
    return len(cleaned_markdown) >= max(80, int(len(original_markdown) * MIN_STRIPPED_MARKDOWN_RATIO))


def strip_html_boilerplate(content: bytes) -> tuple[bytes, dict[str, int]]:
    html = content.decode("utf-8", errors="ignore")
    soup = BeautifulSoup(html, "html.parser")

    for node in soup.find_all(HTML_ALWAYS_DROP_TAGS):
        node.decompose()

    primary_root = select_primary_content_root(soup)
    if primary_root is None:
        return content, {"removedElementCount": 0}

    removed_count = 0
    for node in list(primary_root.find_all(True)):
        if should_remove_dom_node(node):
            node.decompose()
            removed_count += 1

    stripped_html = str(primary_root)
    if not stripped_html.strip():
        return content, {"removedElementCount": 0}
    return stripped_html.encode("utf-8"), {"removedElementCount": removed_count}


def convert_html_with_trafilatura_recall(content: bytes, source_url: str) -> str:
    html = content.decode("utf-8", errors="ignore")
    markdown = trafilatura.extract(
        html,
        url=source_url,
        output_format="markdown",
        favor_recall=True,
        include_tables=True,
        include_links=True,
    )
    normalized = normalize_optional_text(markdown)
    return normalized or ""


def should_use_stripped_markdown(stripped_markdown: str, raw_markdown: str) -> bool:
    if len(stripped_markdown) < MIN_MARKDOWN_CHARS:
        return False
    if not raw_markdown:
        return True
    return len(stripped_markdown) >= int(len(raw_markdown) * MIN_STRIPPED_MARKDOWN_RATIO)


def is_probable_leading_noise_block(block: str) -> bool:
    if is_probable_global_noise_block(block):
        return True

    lines = normalized_block_lines(block)
    if not lines:
        return True
    if any(looks_like_substantive_line(line) for line in lines):
        return False
    short_lines = sum(1 for line in lines if len(line) <= 24)
    if short_lines == len(lines):
        return True
    return len(lines) >= 3 and short_lines >= max(2, int(len(lines) * 0.8))


def is_probable_global_noise_block(block: str) -> bool:
    stripped = block.strip()
    if not stripped:
        return True
    if (
        is_noise_block(stripped)
        or is_promotional_block(stripped)
        or is_related_link_cluster_block(stripped)
        or is_footer_navigation_block(stripped)
        or is_image_only_block(stripped)
    ):
        return True

    lines = normalized_block_lines(stripped)
    if not lines:
        return True
    if any(looks_like_substantive_line(line) for line in lines):
        return False

    short_lines = sum(1 for line in lines if len(line) <= 24)
    return len(lines) >= 8 and short_lines >= max(6, int(len(lines) * 0.8))


def normalized_block_lines(block: str) -> list[str]:
    return [WHITESPACE_PATTERN.sub(" ", line).strip() for line in block.splitlines() if line.strip()]


def looks_like_substantive_line(line: str) -> bool:
    normalized = WHITESPACE_PATTERN.sub(" ", line).strip()
    if len(normalized) >= 48:
        return True
    if re.search(r"[.!?]\s|:\s", normalized) and len(normalized) >= 28:
        return True
    for match in MARKDOWN_LINK_PATTERN.finditer(normalized):
        if is_markdown_image_link(normalized, match.start()):
            continue
        title = match.group("title").strip()
        if len(title) >= 24 and not looks_like_generic_cta_title(title):
            return True
    return False


def normalize_optional_text(value: Any) -> str | None:
    if not isinstance(value, str):
        return None
    normalized = WHITESPACE_PATTERN.sub(" ", value).strip()
    return normalized or None


def build_block_signature(block: str) -> str:
    normalized = normalize_optional_text(block)
    if normalized is None:
        return ""
    signature = MARKDOWN_LINK_PATTERN.sub(lambda match: match.group("title").strip().lower(), normalized.lower())
    signature = re.sub(r"https?://\S+", "", signature)
    signature = WHITESPACE_PATTERN.sub(" ", signature).strip(" -|:")
    return signature


def convert_html_to_markdown(content: bytes, source_url: str | None = None) -> str:
    converter = MarkItDown(enable_plugins=False)
    result = converter.convert_stream(
        BytesIO(content),
        file_extension=".html",
        url=source_url,
    )

    markdown = getattr(result, "text_content", None)
    if not isinstance(markdown, str) or not markdown.strip():
        raise FetchError("MarkItDown returned no markdown content.")

    return markdown


def extract_html_metadata(html: str) -> dict[str, str | None]:
    parser = HtmlMetadataParser()
    parser.feed(html)
    parser.close()
    return {
        "title": parser.meta_title.strip() or parser.title.strip() or None,
        "description": parser.description.strip() or None,
        "byline": parser.byline.strip() or None,
    }


class HtmlMetadataParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.in_title = False
        self.title = ""
        self.meta_title = ""
        self.description = ""
        self.byline = ""

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attr_map = {name.lower(): value for name, value in attrs}
        if tag.lower() == "title":
            self.in_title = True
            return

        if tag.lower() != "meta":
            return

        name = (attr_map.get("name") or attr_map.get("property") or "").lower()
        content = attr_map.get("content") or ""
        if name in {"og:title", "twitter:title"} and content and not self.meta_title:
            self.meta_title = content
        elif name in {"description", "og:description", "twitter:description"} and not self.description:
            self.description = content
        elif name in {"author", "article:author", "byline"} and not self.byline:
            self.byline = content

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() == "title":
            self.in_title = False

    def handle_data(self, data: str) -> None:
        if self.in_title:
            self.title += data


class HtmlBoilerplateStripper(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=False)
        self.output: list[str] = []
        self.stack: list[tuple[str, bool]] = []
        self.removed_element_count = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        tag_name = tag.lower()
        skip = self._should_skip_element(tag_name, attrs)
        self.stack.append((tag_name, skip))
        if skip:
            if not self._parent_is_skipped():
                self.removed_element_count += 1
            return
        self.output.append(self.get_starttag_text() or render_start_tag(tag_name, attrs))

    def handle_startendtag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        tag_name = tag.lower()
        if self._should_skip_element(tag_name, attrs):
            self.removed_element_count += 1
            return
        self.output.append(render_startend_tag(tag_name, attrs))

    def handle_endtag(self, tag: str) -> None:
        tag_name = tag.lower()
        if not self.stack:
            return
        open_tag, skipped = self.stack.pop()
        if skipped:
            return
        self.output.append(f"</{open_tag}>")

    def handle_data(self, data: str) -> None:
        if self._current_is_skipped():
            return
        self.output.append(data)

    def handle_entityref(self, name: str) -> None:
        if self._current_is_skipped():
            return
        self.output.append(f"&{name};")

    def handle_charref(self, name: str) -> None:
        if self._current_is_skipped():
            return
        self.output.append(f"&#{name};")

    def get_html(self) -> str:
        return "".join(self.output)

    def _current_is_skipped(self) -> bool:
        return bool(self.stack and self.stack[-1][1])

    def _parent_is_skipped(self) -> bool:
        return len(self.stack) > 1 and self.stack[-2][1]

    def _should_skip_element(self, tag: str, attrs: list[tuple[str, str | None]]) -> bool:
        if self._current_is_skipped():
            return True
        if tag in HTML_ALWAYS_DROP_TAGS:
            return True
        if tag in HTML_STRUCTURAL_DROP_TAGS:
            return True

        attr_tokens = extract_html_attr_tokens(attrs)
        role = get_html_attr(attrs, "role")
        if role and role.lower() in HTML_BOILERPLATE_ROLES:
            return True
        if has_preserve_signal(attr_tokens):
            return False
        if tag in HTML_DROP_CONTAINER_TAGS and has_boilerplate_signal(attr_tokens):
            return True
        return False


def render_start_tag(tag: str, attrs: list[tuple[str, str | None]]) -> str:
    if not attrs:
        return f"<{tag}>"
    parts = [f"<{tag}"]
    for name, value in attrs:
        if value is None:
            parts.append(f" {name}")
        else:
            escaped = value.replace('"', '&quot;')
            parts.append(f' {name}="{escaped}"')
    parts.append(">")
    return "".join(parts)


def render_startend_tag(tag: str, attrs: list[tuple[str, str | None]]) -> str:
    rendered = render_start_tag(tag, attrs)
    return rendered[:-1] + " />"


def get_html_attr(attrs: list[tuple[str, str | None]], name: str) -> str | None:
    for attr_name, value in attrs:
        if attr_name.lower() == name:
            return value
    return None


def extract_html_attr_tokens(attrs: list[tuple[str, str | None]]) -> set[str]:
    values: list[str] = []
    for attr_name, value in attrs:
        lowered_name = attr_name.lower()
        if lowered_name in {"id", "class", "role", "aria-label", "title", "data-testid", "data-test", "name"} and value:
            values.append(value.lower())
    tokens: set[str] = set()
    for value in values:
        tokens.update(HTML_ATTR_TOKEN_PATTERN.findall(value))
    return tokens


def has_boilerplate_signal(tokens: set[str]) -> bool:
    return any(token in HTML_BOILERPLATE_TERMS for token in tokens)


def has_preserve_signal(tokens: set[str]) -> bool:
    return any(token in HTML_PRESERVE_TERMS for token in tokens)


def select_primary_content_root(soup: BeautifulSoup) -> Tag | None:
    body = soup.body
    if body is None:
        return None

    best_node: Tag | None = None
    best_score = float("-inf")
    body_text_length = visible_text_length(body)
    for node in body.find_all(["main", "article"]):
        score = score_content_candidate(node)
        if score > best_score:
            best_node = node
            best_score = score

    if best_node is None:
        return body
    best_text_length = visible_text_length(best_node)
    if best_text_length < MIN_PRIMARY_CONTAINER_TEXT_CHARS:
        return body
    if body_text_length > 0 and best_text_length < int(body_text_length * 0.3):
        return body
    return best_node


def score_content_candidate(node: Tag) -> float:
    text_length = visible_text_length(node)
    if text_length <= 0:
        return float("-inf")

    attr_tokens = extract_tag_tokens(node)
    paragraph_count = len(node.find_all("p"))
    heading_count = len(node.find_all(["h1", "h2", "h3", "h4", "h5", "h6"]))
    link_density = compute_link_density(node, text_length)
    list_count = len(node.find_all(["ul", "ol"]))

    score = 0.0
    if node.name == "article":
        score += 8.0
    elif node.name == "main":
        score += 6.0
    elif node.name == "section":
        score += 2.0

    if has_preserve_signal(attr_tokens):
        score += 4.0
    if has_boilerplate_signal(attr_tokens):
        score -= 5.0
    if paragraph_count:
        score += min(4.0, paragraph_count * 0.8)
    if heading_count:
        score += min(3.0, heading_count * 0.6)
    if text_length >= 800:
        score += 4.0
    elif text_length >= 400:
        score += 2.0

    score -= min(6.0, link_density * 8.0)
    if list_count > max(2, paragraph_count * 2) and heading_count == 0:
        score -= 3.0
    return score


def visible_text_length(node: Tag) -> int:
    text = node.get_text(" ", strip=True)
    return len(WHITESPACE_PATTERN.sub(" ", text).strip())


def compute_link_density(node: Tag, text_length: int | None = None) -> float:
    total_length = text_length if text_length is not None else visible_text_length(node)
    if total_length <= 0:
        return 0.0
    link_length = 0
    for link in node.find_all("a"):
        link_text = WHITESPACE_PATTERN.sub(" ", link.get_text(" ", strip=True)).strip()
        link_length += len(link_text)
    return link_length / total_length


def should_remove_dom_node(node: Tag) -> bool:
    if node.name is None or getattr(node, "attrs", None) is None:
        return False
    if node.name in HTML_STRUCTURAL_DROP_TAGS:
        return True

    attr_tokens = extract_tag_tokens(node)
    role = (node.get("role") or "").lower()
    text_length = visible_text_length(node)
    link_density = compute_link_density(node, text_length)
    paragraph_count = len(node.find_all("p"))
    heading_count = len(node.find_all(["h1", "h2", "h3", "h4", "h5", "h6"]))
    list_count = len(node.find_all(["ul", "ol"]))

    if role in HTML_BOILERPLATE_ROLES:
        return True
    if has_preserve_signal(attr_tokens):
        return False
    if node.name in HTML_DROP_CONTAINER_TAGS and has_boilerplate_signal(attr_tokens):
        return True
    if text_length == 0:
        return False
    if (
        node.name in {"ul", "ol", "menu"}
        and paragraph_count == 0
        and heading_count == 0
        and text_length < 250
        and link_density >= 0.85
    ):
        return True
    return False


def extract_tag_tokens(node: Tag) -> set[str]:
    if node.name is None or getattr(node, "attrs", None) is None:
        return set()
    values: list[str] = []
    for name in ("id", "class", "role", "aria-label", "title", "data-testid", "data-test", "name"):
        value = node.get(name)
        if isinstance(value, list):
            values.extend(str(item).lower() for item in value)
        elif value:
            values.append(str(value).lower())

    tokens: set[str] = set()
    for value in values:
        tokens.update(HTML_ATTR_TOKEN_PATTERN.findall(value))
    return tokens


def parse_sections(markdown: str) -> list[Section]:
    lines = markdown.splitlines()
    sections: list[Section] = []
    current_heading: str | None = None
    current_level = 0
    current_path: list[str] = []
    current_start_line = 1
    current_content_start = 0

    line_offsets = build_line_offsets(lines)

    def flush(end_line: int, next_char_start: int) -> None:
        nonlocal current_heading, current_level, current_path, current_start_line, current_content_start
        if next_char_start < current_content_start:
            return

        markdown_slice = markdown[current_content_start:next_char_start].strip()
        if not markdown_slice:
            return

        section_id = f"section-{len(sections) + 1}"
        sections.append(
            Section(
                section_id=section_id,
                heading=current_heading,
                level=current_level,
                section_path=list(current_path),
                start_line=current_start_line,
                end_line=end_line,
                char_start=current_content_start,
                char_end=max(current_content_start, next_char_start),
                markdown=markdown_slice,
            )
        )

    for index, line in enumerate(lines, start=1):
        heading_match = HEADING_PATTERN.match(line)
        if heading_match:
            line_char_start = line_offsets[index - 1]
            flush(index - 1, line_char_start)

            current_level = len(heading_match.group(1))
            current_heading = heading_match.group(2).strip()
            current_path = current_path[: current_level - 1]
            current_path.append(current_heading)
            current_start_line = index
            current_content_start = line_char_start

    flush(len(lines), len(markdown))

    if not sections:
        sections.append(
            Section(
                section_id="section-1",
                heading=None,
                level=0,
                section_path=[],
                start_line=1,
                end_line=max(1, len(lines)),
                char_start=0,
                char_end=len(markdown),
                markdown=markdown.strip(),
            )
        )

    return sections


def build_line_offsets(lines: list[str]) -> list[int]:
    offsets: list[int] = []
    cursor = 0
    for line in lines:
        offsets.append(cursor)
        cursor += len(line) + 1
    return offsets


def chunk_sections(
    sections: list[Section],
    source_url: str,
    max_chunk_chars: int,
    chunk_overlap_chars: int,
    max_chunk_count: int,
) -> list[Chunk]:
    chunks: list[Chunk] = []

    for section in sections:
        section_chunks = chunk_section(section, max_chunk_chars, chunk_overlap_chars)
        for section_chunk in section_chunks:
            chunks.append(
                Chunk(
                    chunk_id=f"chunk-{len(chunks) + 1}",
                    ordinal=len(chunks),
                    section_id=section.section_id,
                    section_path=section.section_path,
                    heading=section.heading,
                    source_char_start=section_chunk["source_char_start"],
                    source_char_end=section_chunk["source_char_end"],
                    character_count=len(section_chunk["text"]),
                    advisory_token_estimate=max(1, len(section_chunk["text"]) // 4),
                    overlap_previous_chars=section_chunk["overlap_previous_chars"],
                    overlap_next_chars=section_chunk["overlap_next_chars"],
                    text=section_chunk["text"],
                    provenance={
                        "url": source_url,
                        "sectionPath": section.section_path,
                        "heading": section.heading,
                        "sectionId": section.section_id,
                    },
                )
            )

    return chunks


def chunk_section(section: Section, max_chunk_chars: int, chunk_overlap_chars: int) -> list[dict[str, Any]]:
    text = section.markdown.strip()
    if not text:
        return []

    paragraphs = split_paragraphs(text)
    raw_chunks: list[tuple[int, int, str]] = []
    cursor = 0
    current_parts: list[str] = []
    current_start = 0

    for paragraph in paragraphs:
        paragraph_text = paragraph.strip()
        if not paragraph_text:
            cursor += len(paragraph) + 2
            continue

        if len(paragraph_text) > max_chunk_chars:
            if current_parts:
                chunk_text = "\n\n".join(current_parts)
                raw_chunks.append((current_start, current_start + len(chunk_text), chunk_text))
                current_parts = []

            for large_piece in split_large_text(paragraph_text, max_chunk_chars):
                raw_chunks.append((cursor, cursor + len(large_piece), large_piece))
                cursor += len(large_piece)
            continue

        candidate = "\n\n".join([*current_parts, paragraph_text]) if current_parts else paragraph_text
        if current_parts and len(candidate) > max_chunk_chars:
            chunk_text = "\n\n".join(current_parts)
            raw_chunks.append((current_start, current_start + len(chunk_text), chunk_text))
            current_parts = [paragraph_text]
            current_start = cursor
        else:
            if not current_parts:
                current_start = cursor
            current_parts.append(paragraph_text)

        cursor += len(paragraph) + 2

    if current_parts:
        chunk_text = "\n\n".join(current_parts)
        raw_chunks.append((current_start, current_start + len(chunk_text), chunk_text))

    chunk_dicts: list[dict[str, Any]] = []
    for index, (start, end, chunk_text) in enumerate(raw_chunks):
        previous_overlap = 0
        next_overlap = 0
        expanded_text = chunk_text
        if chunk_overlap_chars > 0 and index > 0:
            previous_tail = raw_chunks[index - 1][2][-chunk_overlap_chars:]
            previous_overlap = len(previous_tail)
            expanded_text = f"{previous_tail}\n{expanded_text}"
        if chunk_overlap_chars > 0 and index < len(raw_chunks) - 1:
            next_head = raw_chunks[index + 1][2][:chunk_overlap_chars]
            next_overlap = len(next_head)
            expanded_text = f"{expanded_text}\n{next_head}"

        chunk_dicts.append(
            {
                "source_char_start": section.char_start + start,
                "source_char_end": min(section.char_start + end, section.char_end),
                "overlap_previous_chars": previous_overlap,
                "overlap_next_chars": next_overlap,
                "text": expanded_text.strip(),
            }
        )

    return chunk_dicts


def split_paragraphs(text: str) -> list[str]:
    return re.split(r"\n\s*\n", text)


def split_large_text(text: str, max_chunk_chars: int) -> list[str]:
    pieces: list[str] = []
    sentences = SENTENCE_BOUNDARY_PATTERN.split(text)
    buffer = ""

    for sentence in sentences:
        candidate = sentence if not buffer else f"{buffer} {sentence}".strip()
        if len(candidate) <= max_chunk_chars:
            buffer = candidate
            continue

        if buffer:
            pieces.append(buffer)
            buffer = ""

        if len(sentence) <= max_chunk_chars:
            buffer = sentence.strip()
            continue

        for line in hard_split(sentence, max_chunk_chars):
            pieces.append(line)

    if buffer:
        pieces.append(buffer)

    return [piece.strip() for piece in pieces if piece.strip()]


def hard_split(text: str, max_chunk_chars: int) -> list[str]:
    parts: list[str] = []
    remaining = text.strip()
    while len(remaining) > max_chunk_chars:
        split_at = remaining.rfind(" ", 0, max_chunk_chars)
        if split_at < max_chunk_chars // 2:
            split_at = max_chunk_chars
        parts.append(remaining[:split_at].strip())
        remaining = remaining[split_at:].strip()

    if remaining:
        parts.append(remaining)

    return parts


def build_selected_chunks(
    chunks: list[Chunk],
    query: str | None,
    top_k: int,
    expansion_budget: int,
) -> list[dict[str, Any]]:
    if not chunks:
        return []

    if not query:
        base_indexes = select_default_chunk_indexes(chunks, top_k)
        scored = [(index, 0.0, "leading substantive chunks") for index in base_indexes]
        expansion_budget = 0
    else:
        scored = rank_chunks(chunks, query)[:top_k]

    selected: list[dict[str, Any]] = []
    for rank, (chunk_index, score, reason) in enumerate(scored, start=1):
        expanded_indexes = expand_indexes(chunk_index, len(chunks), expansion_budget)
        expanded_chunks = [chunks[index] for index in expanded_indexes]
        selected.append(
            {
                "rank": rank,
                "rootChunkId": chunks[chunk_index].chunk_id,
                "score": round(score, 4),
                "reason": reason,
                "expandedChunkIds": [chunk.chunk_id for chunk in expanded_chunks],
                "content": "\n\n".join(chunk.text for chunk in expanded_chunks),
                "contentCharCount": sum(len(chunk.text) for chunk in expanded_chunks),
                "sectionPath": chunks[chunk_index].section_path,
                "expansion": {
                    "budget": expansion_budget,
                    "includedBefore": max(0, chunk_index - expanded_indexes[0]),
                    "includedAfter": max(0, expanded_indexes[-1] - chunk_index),
                },
            }
        )

    return selected


def select_default_chunk_indexes(chunks: list[Chunk], top_k: int) -> list[int]:
    heading_first = [index for index, chunk in enumerate(chunks) if has_heading_line(chunk.text)]
    selected = heading_first[:top_k]
    if len(selected) >= top_k:
        return selected

    preferred = [index for index, chunk in enumerate(chunks) if is_preferred_default_chunk(chunk)]
    for index in preferred:
        if index in selected:
            continue
        selected.append(index)
        if len(selected) >= top_k:
            return selected

    if len(selected) >= top_k:
        return selected

    for index in range(len(chunks)):
        if index in selected:
            continue
        selected.append(index)
        if len(selected) >= top_k:
            break
    return selected


def has_heading_line(text: str) -> bool:
    return any(line.strip().startswith("#") for line in text.splitlines())


def is_preferred_default_chunk(chunk: Chunk) -> bool:
    blocks = split_markdown_blocks(chunk.text)
    if not blocks:
        return False
    if is_probable_link_listing_chunk(chunk.text):
        return False

    substantive_lines = 0
    link_only_blocks = 0
    for block in blocks:
        if is_link_only_block(block):
            link_only_blocks += 1
        lines = normalized_block_lines(block)
        substantive_lines += sum(1 for line in lines if looks_like_substantive_line(line))

    if substantive_lines >= 2:
        return True
    if link_only_blocks == 0 and len(chunk.text) >= 300 and not is_noise_block(chunk.text):
        return True
    return False


def is_probable_link_listing_chunk(text: str) -> bool:
    stripped = text.strip()
    if not stripped:
        return False

    lines = [line.strip() for line in stripped.splitlines() if line.strip()]
    if not lines:
        return False

    heading_lines = sum(1 for line in lines if line.startswith("#"))
    prose_lines = sum(
        1
        for line in lines
        if not line.startswith(("* ", "+ ", "- "))
        and not MARKDOWN_LINK_PATTERN.search(line)
        and len(line) >= 60
    )
    list_lines = sum(1 for line in lines if line.startswith(("* ", "+ ", "- ")))
    link_count = len(list(MARKDOWN_LINK_PATTERN.finditer(stripped)))

    return heading_lines == 0 and prose_lines == 0 and link_count >= 8 and list_lines >= max(5, len(lines) // 3)


def expand_indexes(root_index: int, total: int, expansion_budget: int) -> list[int]:
    indexes = {root_index}
    offset = 1
    while len(indexes) - 1 < expansion_budget and (root_index - offset >= 0 or root_index + offset < total):
        if root_index - offset >= 0 and len(indexes) - 1 < expansion_budget:
            indexes.add(root_index - offset)
        if root_index + offset < total and len(indexes) - 1 < expansion_budget:
            indexes.add(root_index + offset)
        offset += 1

    return sorted(indexes)


def rank_chunks(chunks: list[Chunk], query: str) -> list[tuple[int, float, str]]:
    query_terms = [term for term in tokenize(query) if term not in STOP_WORDS]
    if not query_terms:
        return [(index, 0.0, "query terms were empty after normalization") for index in range(min(DEFAULT_TOP_K, len(chunks)))]

    scored: list[tuple[int, float, str]] = []
    for index, chunk in enumerate(chunks):
        heading_terms = tokenize(" ".join(chunk.section_path))
        body_terms = tokenize(chunk.text)
        body_term_counts = count_terms(body_terms)
        heading_term_counts = count_terms(heading_terms)

        coverage = 0
        score = 0.0
        matched_terms: list[str] = []
        for term in query_terms:
            heading_hits = heading_term_counts.get(term, 0)
            body_hits = body_term_counts.get(term, 0)
            if heading_hits or body_hits:
                coverage += 1
                matched_terms.append(term)
            score += heading_hits * 3.0
            score += body_hits * 1.0

        if query.lower() in chunk.text.lower():
            score += 2.5

        if coverage:
            score += coverage * 2.0

        scored.append(
            (
                index,
                score,
                f"matched {coverage} query terms ({', '.join(matched_terms[:5])})",
            )
        )

    scored.sort(key=lambda item: (-item[1], item[0]))
    return scored


def tokenize(text: str) -> list[str]:
    return TOKEN_PATTERN.findall(text.lower())


def count_terms(terms: list[str]) -> dict[str, int]:
    counts: dict[str, int] = {}
    for term in terms:
        counts[term] = counts.get(term, 0) + 1
    return counts


def build_payload(url: str, fetch_result: FetchResult, options: FetchOptions) -> dict[str, Any]:
    markdown, normalization = normalize_markdown(fetch_result, options)
    paged_markdown, paging, truncation_warnings = paginate_markdown(markdown, options)
    sections = parse_sections(paged_markdown)
    items = extract_items(sections, fetch_result.final_url)
    chunks = chunk_sections(
        sections,
        fetch_result.final_url,
        options.max_chunk_chars,
        options.chunk_overlap_chars,
        options.max_chunk_count,
    )
    paged_chunks, chunk_window, chunk_warnings = paginate_chunks(chunks, options)
    warnings = [*truncation_warnings, *chunk_warnings]
    effective_expansion_budget = (
        options.expansion_budget
        if options.response_mode == "full"
        else min(options.expansion_budget, DEFAULT_COMPACT_EXPANSION_BUDGET)
    )
    selected_chunks = build_selected_chunks(
        chunks,
        options.query,
        options.top_k,
        effective_expansion_budget,
    )
    returned_items, item_window = build_returned_items(
        items,
        options.query,
        options.max_items,
        options.response_mode,
    )

    if options.response_mode != "full":
        selected_chunks = compact_selected_chunks(selected_chunks)

    top_level_headings = [section.heading for section in sections if section.level == 1 and section.heading]
    document_metadata = {
        "title": normalize_document_title(fetch_result.html_metadata.get("title")),
        "description": fetch_result.html_metadata.get("description"),
        "byline": fetch_result.html_metadata.get("byline"),
        "topLevelHeadings": top_level_headings,
        "sectionCount": len(sections),
        "chunkCount": len(chunks),
        "itemCount": len(items),
    }

    payload = {
        "fetch": {
            "requestedUrl": url,
            "finalUrl": fetch_result.final_url,
            "statusCode": fetch_result.status_code,
            "contentType": fetch_result.content_type,
            "fetchedAtUtc": fetch_result.fetched_at_utc,
            "fetchStrategy": fetch_result.fetch_strategy,
            "redirectCount": fetch_result.redirect_count,
            "responseBytes": fetch_result.response_bytes,
            "responseBytesLimitExceeded": fetch_result.response_bytes_limit_exceeded,
        },
        "normalization": normalization,
        "document": document_metadata,
        "guidance": build_response_guidance(url, options, paging, chunk_window, item_window),
        "items": returned_items,
        "itemWindow": item_window,
        "paging": paging,
        "chunkWindow": chunk_window,
        "limits": {
            "responseMode": options.response_mode,
            "maxResponseBytes": options.max_response_bytes,
            "maxNormalizedMarkdownChars": options.max_normalized_markdown_chars,
            "maxChunkCount": options.max_chunk_count,
            "maxChunkChars": options.max_chunk_chars,
            "chunkOverlapChars": options.chunk_overlap_chars,
            "markdownOffsetChars": options.markdown_offset_chars,
            "chunkOffset": options.chunk_offset,
            "maxItems": options.max_items,
            "redirectCap": options.redirect_cap,
            "timeoutSeconds": options.timeout_seconds,
            "allowedContentTypes": list(options.allowed_content_types),
            "sizingMetric": "characters",
        },
        "selectedChunks": selected_chunks,
        "warnings": warnings,
        "rawMarkdown": paged_markdown if options.include_raw_markdown else None,
    }

    if options.response_mode == "full":
        payload["sections"] = [
            {
                "sectionId": section.section_id,
                "heading": section.heading,
                "level": section.level,
                "sectionPath": section.section_path,
                "startLine": section.start_line,
                "endLine": section.end_line,
                "charStart": section.char_start,
                "charEnd": section.char_end,
            }
            for section in sections
        ]
        payload["chunks"] = [to_chunk_dict(chunk) for chunk in paged_chunks]
    else:
        payload["contentIndex"] = {
            "responseMode": options.response_mode,
            "fullResponseMode": "full",
            "sectionsIncluded": False,
            "chunksIncluded": False,
            "itemsIncluded": True,
        }

    return payload


def build_response_guidance(
    url: str,
    options: FetchOptions,
    paging: dict[str, Any],
    chunk_window: dict[str, Any],
    item_window: dict[str, Any],
) -> dict[str, Any]:
    hints: list[str] = [
        "Use selectedChunks first; they are the primary evidence surface for answering questions.",
    ]

    if options.response_mode != "full":
        hints.append(
            "Ask for responseMode='full' only when you need the full chunk or section index, not for normal question answering."
        )

    if item_window.get("truncated"):
        hints.append(
            "If the answer may depend on omitted list items, call the tool again with responseMode='full' or a tighter query."
        )

    if chunk_window.get("truncated"):
        hints.append(
            "If the needed evidence is not in the current chunk window, call the tool again with chunkWindow.nextOptions."
        )

    if paging.get("truncated"):
        hints.append(
            "If the needed evidence may be outside the current markdown window, call the tool again with paging.nextOptions and includeRawMarkdown=true."
        )

    follow_ups: list[dict[str, Any]] = []

    if chunk_window.get("nextOptions") is not None:
        follow_ups.append(
            {
                "purpose": "Get the next full-mode chunk window",
                "arguments": merge_follow_up_arguments(url, options, chunk_window["nextOptions"]),
            }
        )

    if paging.get("nextOptions") is not None:
        follow_ups.append(
            {
                "purpose": "Get the next raw markdown window",
                "arguments": merge_follow_up_arguments(
                    url,
                    options,
                    {**paging["nextOptions"], "includeRawMarkdown": True},
                ),
            }
        )

    if options.response_mode != "full":
        follow_ups.append(
            {
                "purpose": "Escalate to the full indexing surface",
                "arguments": merge_follow_up_arguments(url, options, {"responseMode": "full"}),
            }
        )

    return {
        "primarySurface": "selectedChunks",
        "whenToAskForMore": hints,
        "followUpCalls": follow_ups,
    }


def merge_follow_up_arguments(url: str, options: FetchOptions, overrides: dict[str, Any]) -> dict[str, Any]:
    arguments: dict[str, Any] = {
        "url": url,
        "responseMode": options.response_mode,
        "normalizationMode": options.normalization_mode,
        "topK": options.top_k,
        "maxItems": options.max_items,
        "maxChunkCount": options.max_chunk_count,
        "maxChunkChars": options.max_chunk_chars,
        "chunkOverlapChars": options.chunk_overlap_chars,
        "timeoutSeconds": options.timeout_seconds,
        "fetchMode": options.fetch_mode,
        "expansionBudget": options.expansion_budget,
    }
    if options.query:
        arguments["query"] = options.query
    if options.include_raw_markdown:
        arguments["includeRawMarkdown"] = True
    if options.markdown_offset_chars > 0:
        arguments["markdownOffsetChars"] = options.markdown_offset_chars
    if options.chunk_offset > 0:
        arguments["chunkOffset"] = options.chunk_offset

    arguments.update({key: value for key, value in overrides.items() if value is not None})
    return arguments


def paginate_markdown(markdown: str, options: FetchOptions) -> tuple[str, dict[str, Any], list[str]]:
    total_chars = len(markdown)
    if total_chars == 0:
        return markdown, {
            "truncated": False,
            "truncationReasons": [],
            "window": {
                "offsetChars": 0,
                "returnedChars": 0,
                "totalChars": 0,
                "hasMoreBefore": False,
                "hasMoreAfter": False,
            },
            "nextOptions": None,
        }, []

    offset = min(options.markdown_offset_chars, total_chars)
    limit = options.max_normalized_markdown_chars
    end = min(total_chars, offset + limit)
    paged_markdown = markdown[offset:end]

    truncation_reasons: list[str] = []
    warnings: list[str] = []

    if offset > 0:
        truncation_reasons.append("markdown-window-offset")
        warnings.append(
            f"Returned markdown window starts at offset {offset} because markdownOffsetChars was requested."
        )

    if total_chars > limit:
        truncation_reasons.append("markdown-window-limit")
        warnings.append(
            "Normalized markdown exceeded maxNormalizedMarkdownChars; returned a bounded window. "
            "Use paging.nextOptions to request more."
        )

    next_options = None
    if end < total_chars:
        next_options = {
            "url": None,
            "markdownOffsetChars": end,
            "maxNormalizedMarkdownChars": options.max_normalized_markdown_chars,
        }

    paging = {
        "truncated": bool(truncation_reasons),
        "truncationReasons": truncation_reasons,
        "window": {
            "offsetChars": offset,
            "returnedChars": len(paged_markdown),
            "totalChars": total_chars,
            "hasMoreBefore": offset > 0,
            "hasMoreAfter": end < total_chars,
        },
        "nextOptions": next_options,
    }

    return paged_markdown, paging, warnings


def paginate_chunks(chunks: list[Chunk], options: FetchOptions) -> tuple[list[Chunk], dict[str, Any], list[str]]:
    total_count = len(chunks)
    if total_count == 0:
        return [], {
            "truncated": False,
            "truncationReasons": [],
            "window": {
                "offset": 0,
                "returnedCount": 0,
                "totalCount": 0,
                "hasMoreBefore": False,
                "hasMoreAfter": False,
            },
            "nextOptions": None,
        }, []

    offset = min(options.chunk_offset, total_count)
    end = min(total_count, offset + options.max_chunk_count)
    paged_chunks = chunks[offset:end]
    truncation_reasons: list[str] = []
    warnings: list[str] = []

    if offset > 0:
        truncation_reasons.append("chunk-window-offset")
        if options.response_mode == "full":
            warnings.append(f"Returned chunk window starts at offset {offset} because chunkOffset was requested.")

    if end < total_count:
        truncation_reasons.append("chunk-window-limit")
        if options.response_mode == "full":
            warnings.append(
                "Chunk count exceeded maxChunkCount; returned a bounded chunk window. "
                "Use chunkWindow.nextOptions to request more."
            )

    next_options = None
    if end < total_count:
        next_options = {
            "url": None,
            "responseMode": "full",
            "chunkOffset": end,
            "maxChunkCount": options.max_chunk_count,
        }

    chunk_window = {
        "truncated": bool(truncation_reasons),
        "truncationReasons": truncation_reasons,
        "window": {
            "offset": offset,
            "returnedCount": len(paged_chunks),
            "totalCount": total_count,
            "hasMoreBefore": offset > 0,
            "hasMoreAfter": end < total_count,
        },
        "nextOptions": next_options,
    }

    return paged_chunks, chunk_window, warnings


def normalize_document_title(title: str | None) -> str | None:
    normalized = normalize_optional_text(title)
    if normalized is None:
        return None

    normalized = MULTI_QUOTE_RUN_PATTERN.sub(" ", normalized)

    lowered = normalized.lower()
    noise_positions = [lowered.find(suffix) for suffix in TITLE_NOISE_SUFFIXES if lowered.find(suffix) >= 0]
    if noise_positions:
        normalized = normalized[: min(noise_positions)].strip(" -|:")

    repeated_quote_position = lowered.find("double quotation mark")
    if repeated_quote_position >= 0:
        normalized = normalized[:repeated_quote_position].strip(" -|:")

    normalized = strip_repeated_title_suffix(normalized)
    normalized = normalize_optional_text(normalized)
    if normalized is None:
        return None

    return normalized or None


def strip_repeated_title_suffix(title: str) -> str:
    tokens = title.split()
    if len(tokens) < 3:
        return title

    for index in range(len(tokens) - 1):
        current = normalize_title_token(tokens[index])
        following = normalize_title_token(tokens[index + 1])
        if not current or current != following:
            continue

        suffix_tokens = tokens[index + 1 :]
        if len(suffix_tokens) > 4:
            continue
        if all(is_probable_title_suffix_token(token) for token in suffix_tokens):
            return " ".join(tokens[:index + 1]).strip(" -|:")

    return title


def normalize_title_token(token: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", token.lower())


def is_probable_title_suffix_token(token: str) -> bool:
    stripped = token.strip(" -|:,.!?()[]{}\"'")
    if not stripped:
        return False
    if len(stripped) > 16:
        return False
    if stripped.isupper():
        return True
    return stripped[0].isupper()


def extract_items(sections: list[Section], base_url: str) -> list[dict[str, Any]]:
    items: list[dict[str, Any]] = []
    seen: set[tuple[str, str]] = set()

    for section in sections:
        blocks = split_markdown_blocks(section.markdown)
        if is_navigation_section(section, blocks):
            continue

        for index, block in enumerate(blocks):
            link = select_primary_link(block, section.heading)
            if link is None:
                continue

            title, link_url = link
            title = normalize_item_title(title)
            if title is None:
                continue

            resolved_url = urljoin(base_url, link_url)
            key = (title.lower(), resolved_url)
            if key in seen:
                continue

            source = infer_source(blocks, index)
            published_text = infer_published_text(blocks, index)
            byline = infer_byline(blocks, index)
            summary = infer_summary(blocks, index)
            seen.add(key)
            items.append(
                {
                    "itemId": f"item-{len(items) + 1}",
                    "title": title,
                    "url": resolved_url,
                    "source": source,
                    "publishedText": published_text,
                    "byline": byline,
                    "summary": summary,
                    "sectionPath": section.section_path,
                    "sectionId": section.section_id,
                }
            )

    return items


def build_returned_items(
    items: list[dict[str, Any]],
    query: str | None,
    max_items: int,
    response_mode: str,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    if response_mode == "full":
        return items, {
            "returnedCount": len(items),
            "totalCount": len(items),
            "truncated": False,
            "nextOptions": None,
        }

    effective_max_items = min(max_items, DEFAULT_COMPACT_MAX_ITEMS)
    compact_items = [compact_item(item) for item in items]

    if len(compact_items) <= effective_max_items:
        return compact_items, {
            "returnedCount": len(compact_items),
            "totalCount": len(compact_items),
            "truncated": False,
            "nextOptions": None,
        }

    if not query:
        returned = compact_items[:effective_max_items]
    else:
        ranked_indexes = rank_items(compact_items, query)[:effective_max_items]
        returned = [compact_items[index] for index in ranked_indexes]

    return returned, {
        "returnedCount": len(returned),
        "totalCount": len(compact_items),
        "truncated": len(returned) < len(compact_items),
        "nextOptions": {
            "url": None,
            "responseMode": "full",
        } if len(returned) < len(compact_items) else None,
    }


def compact_item(item: dict[str, Any]) -> dict[str, Any]:
    compacted = dict(item)
    compacted["summary"] = truncate_text(item.get("summary"), DEFAULT_COMPACT_ITEM_SUMMARY_CHARS)
    return compacted


def compact_selected_chunks(selected_chunks: list[dict[str, Any]]) -> list[dict[str, Any]]:
    compacted: list[dict[str, Any]] = []
    for chunk in selected_chunks:
        compacted_chunk = dict(chunk)
        compacted_content = truncate_text(chunk.get("content"), DEFAULT_COMPACT_SELECTED_CHUNK_CHARS)
        compacted_chunk["content"] = compacted_content or ""
        compacted_chunk["contentCharCount"] = len(compacted_chunk["content"])
        compacted.append(compacted_chunk)
    return compacted


def truncate_text(value: Any, limit: int) -> str | None:
    normalized = normalize_optional_text(value)
    if normalized is None or len(normalized) <= limit:
        return normalized

    sentence_matches = list(SENTENCE_BOUNDARY_PATTERN.finditer(normalized))
    cut_at = 0
    for match in sentence_matches:
        if match.start() > limit:
            break
        cut_at = match.start()

    if cut_at < max(limit // 2, 80):
        cut_at = normalized.rfind(" ", 0, limit)
    if cut_at < max(limit // 2, 80):
        cut_at = limit

    truncated = normalized[:cut_at].rstrip(" ,;:")
    return f"{truncated}..."


def rank_items(items: list[dict[str, Any]], query: str) -> list[int]:
    query_terms = [term for term in tokenize(query) if term not in STOP_WORDS]
    if not query_terms:
        return list(range(min(DEFAULT_MAX_ITEMS, len(items))))

    scored: list[tuple[float, int]] = []
    for index, item in enumerate(items):
        haystack = " ".join(
            value for value in [item.get("title"), item.get("summary"), item.get("source")] if isinstance(value, str)
        )
        term_counts = count_terms(tokenize(haystack))
        score = 0.0
        for term in query_terms:
            score += term_counts.get(term, 0)
        scored.append((score, index))

    scored.sort(key=lambda entry: (-entry[0], entry[1]))
    return [index for _, index in scored]


def split_markdown_blocks(markdown: str) -> list[str]:
    return [block.strip() for block in re.split(r"\n\s*\n", markdown) if block.strip()]


def is_navigation_section(section: Section, blocks: list[str]) -> bool:
    heading = (section.heading or "").strip().lower()
    if heading.startswith("follow "):
        return True

    if len(blocks) < 8:
        return False

    link_only_blocks = sum(1 for block in blocks if is_link_only_block(block))
    prose_blocks = sum(1 for block in blocks if is_descriptive_text_block(block))
    return link_only_blocks >= max(6, int(len(blocks) * 0.7)) and prose_blocks == 0


def is_link_only_block(block: str) -> bool:
    stripped = block.strip()
    if not stripped:
        return False
    without_links = MARKDOWN_LINK_PATTERN.sub("", stripped)
    without_links = WHITESPACE_PATTERN.sub(" ", without_links).strip(" -|,:;")
    return bool(MARKDOWN_LINK_PATTERN.search(stripped)) and not without_links


def is_descriptive_text_block(block: str) -> bool:
    stripped = block.strip()
    if not stripped:
        return False
    if MARKDOWN_LINK_PATTERN.search(stripped):
        return False
    if is_noise_block(stripped) or is_image_only_block(stripped):
        return False
    if TIME_LIKE_PATTERN.search(stripped) or stripped.lower().startswith("by "):
        return False
    if is_probable_source(stripped):
        return False
    return len(stripped) >= 40


def select_primary_link(block: str, section_heading: str | None) -> tuple[str, str] | None:
    candidates: list[tuple[str, str]] = []
    for match in MARKDOWN_LINK_PATTERN.finditer(block):
        if is_markdown_image_link(block, match.start()):
            continue

        title = match.group("title").strip()
        url = match.group("url").strip()
        if looks_like_item_title(title, url, section_heading):
            candidates.append((title, url))
            continue

        if looks_like_generic_cta_title(title) and looks_like_section_item_title(section_heading):
            candidates.append((section_heading.strip(), url))

    if not candidates:
        return None

    candidates.sort(key=lambda entry: len(entry[0]), reverse=True)
    return candidates[0]


def looks_like_item_title(title: str, url: str, section_heading: str | None) -> bool:
    normalized_title = WHITESPACE_PATTERN.sub(" ", title).strip()
    if len(normalized_title) < 24:
        return False

    lowered = normalized_title.lower()
    if any(lowered.startswith(prefix) for prefix in SKIP_TITLE_PREFIXES):
        return False

    if section_heading and lowered == section_heading.lower():
        return False

    if url.startswith("./topics/"):
        return False

    if normalized_title.startswith("!"):
        return False

    if is_probable_image_url(url):
        return False

    return True


def looks_like_generic_cta_title(title: str) -> bool:
    normalized_title = WHITESPACE_PATTERN.sub(" ", title).strip().lower()
    return any(normalized_title.startswith(prefix) for prefix in GENERIC_CTA_PREFIXES)


def looks_like_section_item_title(section_heading: str | None) -> bool:
    if not section_heading:
        return False

    normalized_heading = WHITESPACE_PATTERN.sub(" ", section_heading).strip()
    if len(normalized_heading) < 24:
        return False

    lowered = normalized_heading.lower()
    if any(lowered.startswith(prefix) for prefix in SKIP_TITLE_PREFIXES):
        return False
    if lowered.startswith("follow "):
        return False
    return True


def is_markdown_image_link(block: str, match_start: int) -> bool:
    return match_start > 0 and block[match_start - 1] == "!"


def is_probable_image_url(url: str) -> bool:
    lowered = url.lower().strip()
    parsed = urlparse(lowered)
    if parsed.path.endswith(IMAGE_URL_SUFFIXES):
        return True
    if any(hint in parsed.path for hint in IMAGE_URL_HINTS):
        return True
    return any(hint in parsed.query for hint in IMAGE_QUERY_FORMAT_HINTS)


def infer_source(blocks: list[str], index: int) -> str | None:
    for offset in (1, 2, 3, 4):
        previous_index = index - offset
        if previous_index < 0:
            break
        candidate = blocks[previous_index]
        if is_probable_source(candidate):
            return candidate

    for offset in (1, 2):
        next_index = index + offset
        if next_index >= len(blocks):
            break
        candidate = blocks[next_index]
        if candidate.lower().startswith("by "):
            continue
        if MARKDOWN_LINK_PATTERN.search(candidate):
            break
        if is_probable_source(candidate):
            return candidate

    return None


def is_probable_source(block: str) -> bool:
    normalized = block.strip().lower()
    if normalized in SKIP_BLOCK_VALUES:
        return False
    if is_noise_block(block):
        return False
    if HEADING_PATTERN.match(block.strip()):
        return False
    if "\n" in block:
        return False
    if MARKDOWN_LINK_PATTERN.search(block):
        return False
    if is_image_only_block(block):
        return False
    if TIME_LIKE_PATTERN.search(block):
        return False
    if block.lower().startswith("by "):
        return False
    if re.search(r"[,;:!?]", block):
        return False
    word_count = len(block.split())
    return 1 <= word_count <= 4 and len(block) <= 60


def infer_published_text(blocks: list[str], index: int) -> str | None:
    for offset in range(1, 4):
        next_index = index + offset
        if next_index >= len(blocks):
            break
        candidate = blocks[next_index]
        if TIME_LIKE_PATTERN.search(candidate):
            return TIME_LIKE_PATTERN.search(candidate).group(0)
    return None


def infer_byline(blocks: list[str], index: int) -> str | None:
    for offset in range(1, 5):
        next_index = index + offset
        if next_index >= len(blocks):
            break
        candidate = blocks[next_index]
        if candidate.lower().startswith("by "):
            return candidate
    return None


def infer_summary(blocks: list[str], index: int) -> str | None:
    for offset in range(1, 4):
        next_index = index + offset
        if next_index >= len(blocks):
            break
        candidate = blocks[next_index]
        if MARKDOWN_LINK_PATTERN.search(candidate):
            return None
        if is_noise_block(candidate):
            continue
        if is_image_only_block(candidate):
            continue
        if TIME_LIKE_PATTERN.search(candidate):
            continue
        if candidate.lower().startswith("by "):
            continue
        if is_probable_source(candidate):
            continue
        if len(candidate) >= 30:
            return candidate

    for offset in range(1, 3):
        previous_index = index - offset
        if previous_index < 0:
            break
        candidate = blocks[previous_index]
        if HEADING_PATTERN.match(candidate.strip()):
            continue
        if MARKDOWN_LINK_PATTERN.search(candidate):
            continue
        if is_noise_block(candidate) or is_image_only_block(candidate):
            continue
        if TIME_LIKE_PATTERN.search(candidate) or candidate.lower().startswith("by "):
            continue
        if is_probable_source(candidate):
            continue
        if len(candidate) >= 30:
            return candidate

    return None


def is_image_only_block(block: str) -> bool:
    stripped = block.strip()
    return stripped.startswith("![") and "\n" not in stripped


def is_noise_block(block: str) -> bool:
    normalized = WHITESPACE_PATTERN.sub(" ", block).strip().lower()
    if not normalized:
        return True
    return any(pattern in normalized for pattern in NOISE_BLOCK_PATTERNS)


def is_promotional_block(block: str) -> bool:
    normalized = normalize_optional_text(block)
    if normalized is None:
        return False

    lowered = normalized.lower()
    lines = [line.lower() for line in normalized_block_lines(normalized)]
    if any(any(line.startswith(prefix) for prefix in PROMOTIONAL_DISCLAIMER_PREFIXES) for line in lines):
        return True

    promo_hits = sum(term in lowered for term in PROMOTIONAL_CTA_PHRASES)
    if any(prefix in lowered for prefix in PROMOTIONAL_DISCLAIMER_PREFIXES):
        promo_hits += 2

    has_link = MARKDOWN_LINK_PATTERN.search(normalized) is not None
    if promo_hits >= 2 and (has_link or DISCOUNT_PATTERN.search(normalized) is not None):
        return True

    if has_link and promo_hits >= 1 and len(lines) <= 4:
        return True

    return False


def is_related_link_cluster_block(block: str) -> bool:
    normalized = normalize_optional_text(block)
    if normalized is None:
        return False

    lowered = normalized.lower()
    link_matches = list(MARKDOWN_LINK_PATTERN.finditer(normalized))
    if not link_matches:
        return False

    if any(pattern in lowered for pattern in RELATED_SECTION_PATTERNS):
        return True

    generic_link_count = 0
    for match in link_matches:
        title = match.group("title").strip()
        if looks_like_generic_cta_title(title) or title.lower().startswith("read more"):
            generic_link_count += 1

    lines = normalized_block_lines(normalized)
    if generic_link_count >= 1 and len(link_matches) >= 2:
        return True
    if len(link_matches) >= 3 and len(lines) <= 8 and all(len(line) <= 160 for line in lines):
        return True

    return False


def is_footer_navigation_block(block: str) -> bool:
    normalized = normalize_optional_text(block)
    if normalized is None:
        return False

    lowered = normalized.lower()
    lines = normalized_block_lines(normalized)
    link_count = len(list(MARKDOWN_LINK_PATTERN.finditer(normalized)))
    term_hits = sum(term in lowered for term in FOOTER_NAVIGATION_TERMS)
    if term_hits >= 2:
        return True
    if term_hits >= 1 and link_count >= 3 and len(lines) <= 16 and all(len(line) <= 80 for line in lines):
        return True
    return False


def normalize_item_title(title: str | None) -> str | None:
    normalized = normalize_optional_text(title)
    if normalized is None:
        return None

    for pattern in ITEM_TITLE_PREFIX_PATTERNS:
        normalized = pattern.sub("", normalized)

    for pattern in ITEM_TITLE_SUFFIX_PATTERNS:
        normalized = pattern.sub("", normalized)

    normalized = normalized.strip(" -|:")
    return normalized or None


def to_chunk_dict(chunk: Chunk) -> dict[str, Any]:
    payload = asdict(chunk)
    payload["sectionPath"] = payload.pop("section_path")
    payload["sectionId"] = payload.pop("section_id")
    payload["sourceCharStart"] = payload.pop("source_char_start")
    payload["sourceCharEnd"] = payload.pop("source_char_end")
    payload["characterCount"] = payload.pop("character_count")
    payload["advisoryTokenEstimate"] = payload.pop("advisory_token_estimate")
    payload["overlapPreviousChars"] = payload.pop("overlap_previous_chars")
    payload["overlapNextChars"] = payload.pop("overlap_next_chars")
    return payload


async def fetch_and_prepare(url: str, arguments: dict[str, Any]) -> dict[str, Any]:
    options = normalize_options(arguments)
    fetch_result = await fetch_url(url, options)
    return build_payload(url, fetch_result, options)


def summarize_payload(payload: dict[str, Any]) -> str:
    document = payload.get("document", {})
    chunk_count = int(document.get("chunkCount") or len(payload.get("chunks", [])))
    item_count = int(document.get("itemCount") or len(payload.get("items", [])))
    selected_count = len(payload["selectedChunks"])
    title = payload["document"].get("title") or payload["fetch"]["finalUrl"]
    paging = payload.get("paging", {})
    chunk_window = payload.get("chunkWindow", {})
    partial = " Returned a bounded partial result." if (paging.get("truncated") or chunk_window.get("truncated")) else ""
    return (
        f"Retrieved {chunk_count} chunks from {title}. Extracted {item_count} items. "
        f"Selected {selected_count} retrieval windows.{partial}"
    )
