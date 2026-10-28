from __future__ import annotations

from pathlib import Path

import pytest

from kautuka_web_tools.pipeline import FetchOptions, build_payload, build_selected_chunks, chunk_sections, normalize_markdown, normalize_options, parse_sections, rank_chunks


def test_parse_sections_detects_heading_hierarchy() -> None:
    markdown = "# Overview\n\nIntro paragraph.\n\n## Details\n\nSecond paragraph."

    sections = parse_sections(markdown)

    assert len(sections) == 2
    assert sections[0].heading == "Overview"
    assert sections[1].section_path == ["Overview", "Details"]


def test_chunk_sections_respects_character_limit() -> None:
    markdown = "# Overview\n\n" + ("Sentence one. " * 120)
    sections = parse_sections(markdown)

    chunks = chunk_sections(
        sections,
        "https://example.com/article",
        max_chunk_chars=500,
        chunk_overlap_chars=50,
        max_chunk_count=20,
    )

    assert len(chunks) > 1
    assert all(chunk.character_count <= 500 + 100 for chunk in chunks)


def test_rank_and_expand_selected_chunks_is_bounded() -> None:
    markdown = "# Overview\n\nAlpha beta gamma.\n\n## Retrieval\n\nRetrieval scoring retrieves child chunks first.\n\n## Limits\n\nCharacter counts are authoritative."
    sections = parse_sections(markdown)
    chunks = chunk_sections(
        sections,
        "https://example.com/article",
        max_chunk_chars=250,
        chunk_overlap_chars=10,
        max_chunk_count=20,
    )

    ranked = rank_chunks(chunks, "retrieval child chunks")
    assert ranked[0][1] > 0

    selected = build_selected_chunks(chunks, "retrieval child chunks", top_k=1, expansion_budget=1)
    assert len(selected) == 1
    assert len(selected[0]["expandedChunkIds"]) <= 2


def test_build_payload_from_fixture_html() -> None:
    fixture_path = Path(__file__).parent / "fixtures" / "sample_article.html"
    html = fixture_path.read_bytes()
    options = FetchOptions(
        include_raw_markdown=True,
        query="character counts",
        max_chunk_chars=700,
        response_mode="full",
    )

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/html",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(html),
        body=html,
        html_metadata={
            "title": "Kautuka Retrieval Test Article",
            "description": "Fixture content for retrieval and chunking tests.",
            "byline": "Kautuka Test Harness",
        },
    )

    payload = build_payload("https://example.com/article", fetch_result, options)

    assert payload["document"]["title"] == "Kautuka Retrieval Test Article"
    assert payload["chunks"]
    assert payload["items"] == []
    assert payload["selectedChunks"]
    assert payload["rawMarkdown"] is not None
    assert payload["paging"]["truncated"] is False


def test_normalize_options_uses_character_limits_and_defaults() -> None:
    options = normalize_options({"url": "https://example.com", "topK": 50, "maxChunkChars": 100})

    assert options.top_k == 10
    assert options.max_chunk_chars >= 400


def test_normalize_markdown_uses_trafilatura_clean_html_and_metadata(monkeypatch: pytest.MonkeyPatch) -> None:
    from kautuka_web_tools import pipeline
    from kautuka_web_tools.pipeline import FetchResult

    seen: dict[str, str] = {}

    def fake_convert_html_with_trafilatura_recall(content: bytes, source_url: str) -> str:
        seen["recall_url"] = source_url
        return "# Clean Title\n\n" + ("Clean body. " * 25)

    def fake_convert_html_to_markdown(content: bytes, source_url: str | None = None) -> str:
        seen["html"] = content.decode("utf-8")
        seen["url"] = source_url or ""
        return "# Raw Title\n\n" + ("Raw body. " * 60)

    monkeypatch.setattr(pipeline, "convert_html_with_trafilatura_recall", fake_convert_html_with_trafilatura_recall)
    monkeypatch.setattr(pipeline, "convert_html_to_markdown", fake_convert_html_to_markdown)

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/html",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=100,
        body=b"<html><body>Raw body</body></html>",
        html_metadata={"title": "Raw Title", "description": None, "byline": None},
    )

    markdown, normalization = normalize_markdown(fetch_result, FetchOptions(normalization_mode="trafilatura_recall"))

    assert markdown.startswith("Clean body.")
    assert "Clean body." in markdown
    assert seen["url"] == "https://example.com/article"
    assert seen["recall_url"] == "https://example.com/article"
    assert normalization["normalizer"] == "trafilatura_recall"
    assert normalization["preCleaner"] == "trafilatura"
    assert normalization["preCleanerApplied"] is True
    assert normalization["preCleanerRejectedReason"] is None
    assert fetch_result.html_metadata["title"] == "Raw Title"
    assert fetch_result.html_metadata["description"] is None
    assert fetch_result.html_metadata["byline"] is None


def test_normalize_markdown_falls_back_to_raw_html_when_trafilatura_clean_html_is_unavailable(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from kautuka_web_tools import pipeline
    from kautuka_web_tools.pipeline import FetchResult

    seen: dict[str, str] = {}

    def fake_convert_html_with_trafilatura_recall(content: bytes, source_url: str) -> str:
        seen["recall_url"] = source_url
        return ""

    def fake_convert_html_to_markdown(content: bytes, source_url: str | None = None) -> str:
        seen["html"] = content.decode("utf-8")
        return "# Raw Title\n\nRaw body."

    monkeypatch.setattr(pipeline, "convert_html_with_trafilatura_recall", fake_convert_html_with_trafilatura_recall)
    monkeypatch.setattr(pipeline, "convert_html_to_markdown", fake_convert_html_to_markdown)

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/html",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=100,
        body=b"<html><body>Raw body</body></html>",
        html_metadata={"title": "Raw Title", "description": None, "byline": None},
    )

    markdown, normalization = normalize_markdown(fetch_result, FetchOptions(normalization_mode="trafilatura_recall"))

    assert markdown == "# Raw Title\n\nRaw body."
    assert seen["html"] == "<html><body>Raw body</body></html>"
    assert seen["recall_url"] == "https://example.com/article"
    assert normalization["normalizer"] == "markitdown"
    assert normalization["preCleaner"] == "trafilatura"
    assert normalization["preCleanerApplied"] is False
    assert normalization["preCleanerRejectedReason"] == "trafilatura-markdown-too-thin"


def test_normalize_markdown_falls_back_when_trafilatura_markdown_is_too_small(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from kautuka_web_tools import pipeline
    from kautuka_web_tools.pipeline import FetchResult

    seen: list[str] = []

    def fake_convert_html_with_trafilatura_recall(content: bytes, source_url: str) -> str:
        seen.append(source_url)
        return "short"

    def fake_convert_html_to_markdown(content: bytes, source_url: str | None = None) -> str:
        html = content.decode("utf-8")
        seen.append(html)
        return "# Raw Title\n\nRaw body with enough content to keep the raw fallback path active."

    monkeypatch.setattr(pipeline, "convert_html_with_trafilatura_recall", fake_convert_html_with_trafilatura_recall)
    monkeypatch.setattr(pipeline, "convert_html_to_markdown", fake_convert_html_to_markdown)

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/html",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=100,
        body=b"<html><body>Raw body</body></html>",
        html_metadata={"title": "Raw Title", "description": None, "byline": None},
    )

    markdown, normalization = normalize_markdown(fetch_result, FetchOptions(normalization_mode="trafilatura_recall"))

    assert markdown.startswith("# Raw Title")
    assert len(seen) == 2
    assert normalization["normalizer"] == "markitdown"
    assert normalization["preCleanerApplied"] is False
    assert normalization["preCleanerRejectedReason"] == "trafilatura-markdown-too-thin"


def test_normalize_markdown_removes_promotional_blocks_from_markdown_input() -> None:
    from kautuka_web_tools.pipeline import FetchResult

    markdown = """# Main article

This is the actual article body with enough substantive text to survive cleanup.

Sponsored Content

[Shop now for the premium bundle](https://example.com/promo)

Limited time special offer with 30% off when you subscribe to our newsletter.

More reporting from the newsroom with enough detail to remain in the cleaned article.
"""

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Example", "description": None, "byline": None},
    )

    cleaned_markdown, normalization = normalize_markdown(fetch_result, FetchOptions())

    assert "Sponsored Content" not in cleaned_markdown
    assert "30% off" not in cleaned_markdown
    assert "actual article body" in cleaned_markdown
    assert normalization["contentPostProcessorApplied"] is True
    assert normalization["contentPostProcessorRemovedBlocks"] >= 2


def test_build_payload_normalizes_noisy_document_title() -> None:
    html = b"<html><head><title>ignored</title></head><body><h1>Overview</h1><p>Body.</p></body></html>"
    options = FetchOptions(max_chunk_chars=300)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/html",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(html),
        body=html,
        html_metadata={
            "title": "Microsoft - AI, Cloud, Productivity, Computing, Gaming & AppsYour Privacy Choices Opt-Out IconYour Privacy Choices Opt-Out Icon",
            "description": "Example description.",
            "byline": None,
        },
    )

    payload = build_payload("https://example.com/article", fetch_result, options)

    assert payload["document"]["title"] == "Microsoft - AI, Cloud, Productivity, Computing, Gaming & Apps"


def test_build_payload_returns_bounded_partial_window_when_markdown_exceeds_limit() -> None:
    markdown = "# Overview\n\n" + ("abcdefghij " * 2000)
    options = FetchOptions(max_normalized_markdown_chars=500, max_chunk_chars=250)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Example", "description": None, "byline": None},
        response_bytes_limit_exceeded=True,
    )

    payload = build_payload("https://example.com/article", fetch_result, options)

    assert payload["fetch"]["responseBytesLimitExceeded"] is True
    assert payload["paging"]["truncated"] is True
    assert payload["paging"]["window"]["returnedChars"] == 500
    assert payload["paging"]["window"]["hasMoreAfter"] is True
    assert payload["paging"]["nextOptions"]["markdownOffsetChars"] == 500
    assert payload["warnings"]


def test_build_payload_honors_markdown_offset_paging() -> None:
    markdown = "# Overview\n\n" + ("abcdefghij " * 200)
    options = FetchOptions(max_normalized_markdown_chars=120, markdown_offset_chars=120, max_chunk_chars=120)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Example", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/article", fetch_result, options)

    assert payload["paging"]["window"]["offsetChars"] == 120
    assert payload["paging"]["window"]["hasMoreBefore"] is True


def test_build_payload_extracts_generic_list_items_from_markdown() -> None:
    markdown = """# Headlines

Example Times

[Major policy shift reshapes the national debate overnight](https://example.com/story-1)

2 hours ago

By Jane Doe

Regional Wire

[Second long-form update explains the market reaction in detail](https://example.com/story-2)

Yesterday
"""
    options = FetchOptions(max_normalized_markdown_chars=1000, max_chunk_chars=300)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/headlines",
        final_url="https://example.com/headlines",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Headlines", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/headlines", fetch_result, options)

    assert len(payload["items"]) == 2
    assert payload["items"][0]["title"] == "Major policy shift reshapes the national debate overnight"
    assert payload["items"][0]["source"] == "Example Times"
    assert payload["items"][0]["publishedText"] == "2 hours ago"
    assert payload["items"][0]["byline"] == "By Jane Doe"
    assert payload["items"][1]["source"] == "Regional Wire"


def test_build_payload_skips_image_links_and_noise_metadata_in_items() -> None:
    markdown = """# Highlights

Trace Id is missing

![A Surface Laptop, 13.8 and 15-inch (8th Edition) | Intel.](https://cdn.example.com/is/image/microsoftcorp/Card-Feature-Surface-Laptop?fmt=png-alpha)

[Accessible AI starts with language for more people](https://example.com/story-1)

Your Privacy Choices

This work focuses on making AI systems more usable across low-resource languages.
"""
    options = FetchOptions(max_normalized_markdown_chars=1000, max_chunk_chars=300)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/highlights",
        final_url="https://example.com/highlights",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Highlights", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/highlights", fetch_result, options)

    assert len(payload["items"]) == 1
    assert payload["items"][0]["title"] == "Accessible AI starts with language for more people"
    assert payload["items"][0]["url"] == "https://example.com/story-1"
    assert payload["items"][0]["source"] is None
    assert payload["items"][0]["summary"] == (
        "This work focuses on making AI systems more usable across low-resource languages."
    )


def test_build_payload_skips_navigation_dense_sections_from_items() -> None:
    markdown = """# Highlights

[Accessible AI starts with language for more people](https://example.com/story-1)

This work focuses on making AI systems more usable across low-resource languages.

## Follow Example

[Explore Microsoft products and services globally](https://example.com/products)

[Educator training and development resources](https://example.com/education)

[Support for AI marketplace apps and tools](https://example.com/marketplace)

[Windows 11 apps for productivity and gaming](https://example.com/windows)

[Microsoft Teams for modern collaboration at work](https://example.com/teams)

[Azure cloud solutions for enterprise workloads](https://example.com/azure)

[Dynamics 365 for intelligent business workflows](https://example.com/dynamics)

[Microsoft developer tools and learning resources](https://example.com/developer)
"""
    options = FetchOptions(max_normalized_markdown_chars=2000, max_chunk_chars=300)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/highlights",
        final_url="https://example.com/highlights",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Highlights", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/highlights", fetch_result, options)

    assert len(payload["items"]) == 1
    assert payload["items"][0]["title"] == "Accessible AI starts with language for more people"


def test_build_payload_uses_section_heading_for_generic_cta_links() -> None:
    markdown = """## Diving smarter to save marine life

Finding lost fishing nets has long been a game of luck, but with AI, divers can locate them faster.

[Read the story](https://example.com/story-1)
"""
    options = FetchOptions(max_normalized_markdown_chars=1000, max_chunk_chars=300)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/highlights",
        final_url="https://example.com/highlights",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Highlights", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/highlights", fetch_result, options)

    assert len(payload["items"]) == 1
    assert payload["items"][0]["title"] == "Diving smarter to save marine life"
    assert payload["items"][0]["url"] == "https://example.com/story-1"
    assert payload["items"][0]["summary"] == (
        "Finding lost fishing nets has long been a game of luck, but with AI, divers can locate them faster."
    )


def test_build_payload_normalizes_item_titles_from_suffix_and_price_dot_noise() -> None:
    markdown = r"""## $ • • • • • • on Surface Pro, 13-inch

A flexible design and larger display, ideal for those wanting the portability of a tablet and the power of a laptop.

[Learn more](https://example.com/surface-pro)

[Australian tourist dies after falling on Inca Trail in Peru chevron\_right](https://example.com/story-1)

The Guardian
"""
    options = FetchOptions(max_normalized_markdown_chars=1000, max_chunk_chars=300)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/highlights",
        final_url="https://example.com/highlights",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Highlights", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/highlights", fetch_result, options)

    assert payload["items"][0]["title"] == "Surface Pro, 13-inch"
    assert payload["items"][1]["title"] == "Australian tourist dies after falling on Inca Trail in Peru"


def test_build_payload_uses_following_source_when_lead_link_has_no_prior_source() -> None:
    markdown = r"""# Your briefing

[Australian tourist dies after falling on Inca Trail in Peru chevron\_right](https://example.com/story-1)

The Guardian

By Nick Visser
"""
    options = FetchOptions(max_normalized_markdown_chars=1000, max_chunk_chars=300)

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/briefing",
        final_url="https://example.com/briefing",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Briefing", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/briefing", fetch_result, options)

    assert len(payload["items"]) == 1
    assert payload["items"][0]["title"] == "Australian tourist dies after falling on Inca Trail in Peru"
    assert payload["items"][0]["source"] == "The Guardian"
    assert payload["items"][0]["byline"] == "By Nick Visser"


def test_build_payload_compact_caps_item_count_and_selected_chunk_size() -> None:
    article_body = " ".join(f"Sentence {index}." for index in range(1, 220))
    related_items = "\n\n".join(
        [
            f"[Related story number {index} with enough words to look substantive](https://example.com/story-{index})\n\n"
            + ("This summary sentence is intentionally long to trigger compact truncation. " * 8)
            for index in range(1, 15)
        ]
    )
    markdown = f"# Main article\n\n{article_body}\n\n## Related\n\n{related_items}"
    options = FetchOptions(
        query="sentence article",
        max_normalized_markdown_chars=6000,
        max_chunk_chars=500,
        max_items=40,
        response_mode="compact",
        expansion_budget=4,
    )

    from kautuka_web_tools.pipeline import FetchResult

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Example", "description": None, "byline": None},
    )

    payload = build_payload("https://example.com/article", fetch_result, options)

    assert 1 <= len(payload["items"]) <= 8
    assert payload["itemWindow"]["returnedCount"] == len(payload["items"])
    assert all(
        item["summary"] is None or len(item["summary"]) <= 243
        for item in payload["items"]
    )
    assert payload["selectedChunks"]
    assert all(chunk["contentCharCount"] <= 903 for chunk in payload["selectedChunks"])
    assert all(len(chunk["expandedChunkIds"]) <= 3 for chunk in payload["selectedChunks"])


def test_normalize_markdown_dedupes_repeated_windows_and_related_footer_blocks() -> None:
    from kautuka_web_tools.pipeline import FetchResult

    repeated_window = """# How Urban India's Weekend Obsession Became A $50 Million Market

## Industry estimates peg India's pickleball market at nearly Rs 7,500 crore by 2030.

### From Niche Sport To Urban Lifestyle

According to Siddhant Jatia, India had roughly 200 dedicated pickleball courts in 2024.
"""
    markdown = f"""
{repeated_window}

{repeated_window}

ADVERTISEMENT - SCROLL TO CONTINUE

### [Opinion | Rubio Is In India, But Trump's America Just Launched Its Sharpest Attack On Indian Immigrants](https://www.ndtv.com/opinion/example-1?utm_source=ndtv.com&utm_medium=spotlight-page&utm_campaign=related-section)

[Read More](https://www.ndtv.com/opinion/example-1?utm_source=ndtv.com&utm_medium=spotlight-page&utm_campaign=related-section)

[About Us](https://www.ndtv.com/about)
[Advertise](https://www.ndtv.com/advertise)
[Partner Content](https://www.ndtv.com/partner-content)
[Privacy Policy](https://www.ndtv.com/privacy)

It started casually. A few games after work. A cheaper alternative to expensive fitness memberships.
"""

    fetch_result = FetchResult(
        url="https://example.com/article",
        final_url="https://example.com/article",
        status_code=200,
        content_type="text/markdown",
        fetched_at_utc="2026-05-22T00:00:00+00:00",
        fetch_strategy="http",
        redirect_count=0,
        response_bytes=len(markdown.encode("utf-8")),
        body=markdown.encode("utf-8"),
        html_metadata={"title": "Example", "description": None, "byline": None},
    )

    cleaned_markdown, normalization = normalize_markdown(fetch_result, FetchOptions())

    assert cleaned_markdown.count("How Urban India's Weekend Obsession Became A $50 Million Market") == 1
    assert "ADVERTISEMENT - SCROLL TO CONTINUE" not in cleaned_markdown
    assert "utm_campaign=related-section" not in cleaned_markdown
    assert "Partner Content" not in cleaned_markdown
    assert "It started casually." in cleaned_markdown
    assert normalization["contentPostProcessorApplied"] is True
    assert normalization["contentPostProcessorRemovedBlocks"] >= 3
