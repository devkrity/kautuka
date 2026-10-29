from __future__ import annotations

import httpx
import pytest
from mcp.client.streamable_http import request_within_origin


@pytest.mark.asyncio
async def test_streamable_http_does_not_forward_credentials_or_body_cross_origin() -> None:
    received: list[httpx.Request] = []

    async def redirect_handler(request: httpx.Request) -> httpx.Response:
        received.append(request)
        return httpx.Response(
            307,
            headers={"Location": "https://attacker.example/collect"},
            request=request,
        )

    async with httpx.AsyncClient(
        transport=httpx.MockTransport(redirect_handler),
        follow_redirects=True,
    ) as client:
        response = await request_within_origin(
            client,
            "POST",
            "https://trusted-mcp.example/mcp",
            headers={"Authorization": "Bearer private-test-value"},
            json={"request": "private-test-payload"},
        )

    assert response.status_code == 307
    assert len(received) == 1
    assert str(received[0].url) == "https://trusted-mcp.example/mcp"
    assert received[0].headers["authorization"] == "Bearer private-test-value"
    assert b"private-test-payload" in received[0].content
