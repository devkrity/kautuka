#!/usr/bin/env python3
"""Bind staged image archives to config IDs before the release inventory is signed."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import tarfile
import tempfile

MAX_JSON = 4 * 1024 * 1024
DIGEST = re.compile(r"sha256:[a-fA-F0-9]{64}")
ARCHIVE = re.compile(r"[a-z0-9][a-z0-9.-]*\.(?:tar|oci)")


def read_json(archive, name, digest=None):
    member = archive.getmember(name)
    if not member.isfile() or member.size > MAX_JSON:
        raise ValueError("Image archive JSON must be a bounded regular file: " + name)
    with archive.extractfile(member) as stream:
        data = stream.read(MAX_JSON + 1)
    if len(data) > MAX_JSON:
        raise ValueError("Image archive JSON is too large")
    actual = "sha256:" + hashlib.sha256(data).hexdigest()
    if digest is not None and actual.lower() != digest.lower():
        raise ValueError("Image archive blob digest mismatch: " + name)
    return json.loads(data), actual


def blob(archive, descriptor):
    digest = descriptor.get("digest")
    if not isinstance(digest, str) or not DIGEST.fullmatch(digest):
        raise ValueError("Image archive descriptor must use SHA-256")
    return read_json(archive, "blobs/sha256/" + digest.split(":", 1)[1], digest)


def archive_identity(filename, platform):
    with tarfile.open(filename, "r:*") as archive:
        names = [member.name for member in archive.getmembers()]
        if len(names) != len(set(names)):
            raise ValueError("Image archive contains duplicate members")
        if "index.json" in names:
            manifest, _ = read_json(archive, "index.json")
            for _ in range(4):
                if "manifests" not in manifest:
                    break
                descriptors = manifest["manifests"]
                if not isinstance(descriptors, list) or len(descriptors) != 1:
                    raise ValueError("Release archives must contain one selected platform image")
                manifest, _ = blob(archive, descriptors[0])
            if "config" not in manifest:
                raise ValueError("OCI archive has no selected image config")
            config, config_id = blob(archive, manifest["config"])
        else:
            manifest, _ = read_json(archive, "manifest.json")
            if not isinstance(manifest, list) or len(manifest) != 1:
                raise ValueError("Docker archives must contain one image")
            config, config_id = read_json(archive, manifest[0]["Config"])
        actual_platform = str(config.get("os")) + "/" + str(config.get("architecture"))
        if actual_platform != platform:
            raise ValueError("Archive platform differs from release target: " + actual_platform)
    with pathlib.Path(filename).open("rb") as stream:
        archive_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    return {"archiveSha256": archive_hash, "platform": platform, "localImageId": config_id}


def bind_inventory(inventory, archive_root, platform):
    if platform not in ("linux/amd64", "linux/arm64"):
        raise ValueError("Unsupported appliance image platform")
    images = inventory.get("images")
    if not isinstance(images, list):
        raise ValueError("Runtime inventory must contain images")
    result = dict(inventory)
    result["images"] = []
    seen = set()
    for original in images:
        if not isinstance(original, dict) or not isinstance(original.get("id"), str) or original["id"] in seen:
            raise ValueError("Runtime inventory IDs must be unique")
        seen.add(original["id"])
        entry = dict(original)
        name = entry.get("archive")
        if name is not None:
            if not isinstance(name, str) or not ARCHIVE.fullmatch(name):
                raise ValueError("Image archive name must be a portable basename")
            source = entry.get("image")
            if not isinstance(source, str) or not re.fullmatch(r"[^\s@]+@sha256:[a-fA-F0-9]{64}", source):
                raise ValueError("Release image sources must be pinned repository digests")
            entry.update(archive_identity(pathlib.Path(archive_root) / name, platform))
        result["images"].append(entry)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", required=True, type=pathlib.Path)
    parser.add_argument("--archive-root", required=True, type=pathlib.Path)
    parser.add_argument("--platform", required=True, choices=("linux/amd64", "linux/arm64"))
    parser.add_argument("--output", required=True, type=pathlib.Path)
    options = parser.parse_args()
    result = bind_inventory(json.loads(options.inventory.read_text(encoding="utf-8")), options.archive_root, options.platform)
    options.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=options.output.parent, delete=False) as stream:
            temporary = pathlib.Path(stream.name)
            json.dump(result, stream, indent=2)
            stream.write("\n")
        temporary.chmod(0o644)
        os.replace(temporary, options.output)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


if __name__ == "__main__":
    main()
