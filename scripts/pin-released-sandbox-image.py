#!/usr/bin/env python3
"""Insert the released kautuka-base digest into runtime-images.json."""

import json
import sys
from pathlib import Path


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: pin-released-sandbox-image.py runtime-images.json IMAGE@sha256:...")
    path = Path(sys.argv[1])
    image = sys.argv[2].strip()
    prefix = "ghcr.io/devkrity/kautuka-base@sha256:"
    if not image.startswith(prefix) or len(image) != len(prefix) + 64:
        raise SystemExit(f"refusing to pin sandbox image that is not a kautuka-base digest: {image}")
    digest = image.removeprefix(prefix)
    if any(character not in "0123456789abcdef" for character in digest):
        raise SystemExit(f"sandbox digest is not 64 hex characters: {image}")

    data = json.loads(path.read_text(encoding="utf-8"))
    images = [entry for entry in data.get("images", []) if entry.get("id") != "kautuka-base"]
    images.insert(0, {"id": "kautuka-base", "image": image, "archive": "kautuka-base.tar"})
    data["images"] = images
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
