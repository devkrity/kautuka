#!/usr/bin/env python3
"""Capture and restore Kautuka's explicitly supported application database set.

This utility is called only by the root-owned update helper after host writers
have been stopped. It deliberately excludes workspaces, attachments, Podman
storage, updater state, logs, and arbitrary service directories.
"""
from __future__ import annotations

import argparse
import errno
import hashlib
import json
import os
import re
import shutil
import sqlite3
import stat
import sys
import tempfile
import secrets
from datetime import datetime, timezone
from typing import NoReturn
from pathlib import Path

FORMAT = 2
DATABASES = (
    "kautuka.db",
    "kautuka-usage.db",
    "channel-outbox.db",
    "channel-conversations.db",
    "credentialsHost/vault/kautuka.credentials.db",
    "channelHost/channelhost.db",
)
ROOT_KEYS = ("kautuka.key", "gateway-auth.key")
SERVICE_KEYS = {
    "manager": (".ca.key", ".ca.cer", ".manager-gateway-client.pfx", ".manager-updateservice-client.pfx"),
    "gateway": (".ca.key", ".ca.cer", ".updateservice-client.pfx", ".credentials-client.pfx", ".channel-client.pfx"),
    "channelHost": (".ca.key", ".ca.cer", ".channel-client.pfx", ".channel-delegation.pfx", ".channel-delegation.cer"),
    "credentialsHost/transport": (".ca.key", ".ca.cer", ".credentials-client.pfx"),
    "updateService": (".ca.key", ".ca.cer", ".updateservice-client.pfx"),
}
CHANNEL_SECURITY_FILES = {
    "security/channel/channel-private": ("channel-server.pfx",),
    "security/channel/gateway-private": ("gateway-channel-client.pfx", "gateway-channel-delegation.pfx"),
    "security/channel/gateway-trust": ("channel-ca.cer",),
    "security/channel/channel-trust": (
        "channel-ca.cer", "gateway-channel-client.cer", "gateway-channel-delegation.cer",
        "gateway-channel-client.cer.next", "gateway-channel-client.cer.previous",
        "gateway-channel-delegation.cer.next", "gateway-channel-delegation.cer.previous",
        "channel-security-rotation.json",
    ),
    "security/channel/offline-authority": (
        "channel-ca.key", "channel-ca.cer", ".ca.key", ".ca.cer",
        ".rotation-client.pfx", ".rotation-delegation.pfx", ".rotation-delegation.cer",
    ),
}


def fail(message: str) -> NoReturn:
    raise RuntimeError(message)


def safe_regular(path: Path) -> os.stat_result:
    st = path.lstat()
    if not stat.S_ISREG(st.st_mode):
        fail(f"unsupported non-regular application-state path: {path}")
    return st


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def fsync_file(path: Path) -> None:
    with path.open("rb+") as stream:
        os.fsync(stream.fileno())


def fsync_dir(path: Path) -> None:
    if os.name == "nt":
        return
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def db_integrity(path: Path) -> None:
    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=10)
    try:
        results = [row[0] for row in db.execute("PRAGMA integrity_check")]
        if results != ["ok"]:
            fail(f"SQLite integrity check failed for {path}: {results[:4]}")
    finally:
        db.close()


def validate_credential_material(data_root: Path, file_root: Path | None = None) -> None:
    """Ensure every credential envelope is decryptable by the captured ring."""
    prefix = "KAUTUKA-CREDENTIAL-V2:"
    db_path = data_root / "credentialsHost/vault/kautuka.credentials.db"
    source = file_root or data_root
    key_root = source / "credentialsHost/vault/vault-keys"
    if not db_path.exists():
        return
    active_path = key_root / "active"
    if not active_path.is_file():
        # A freshly provisioned appliance can have a database before the vault
        # has ever created a key. Preserve that state only when there are no
        # credentials, rotation records, or orphaned keys to lose.
        if any(key_root.glob("*.key")):
            fail("credential key ring has keys without an active marker")
        db = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True, timeout=10)
        try:
            for table in ("user_credentials", "system_credentials", "credential_key_rotation"):
                exists = db.execute(
                    "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (table,)
                ).fetchone()
                if exists and db.execute(f"SELECT 1 FROM {table} LIMIT 1").fetchone():
                    fail("credentials database contains data without its active vault-key marker")
        finally:
            db.close()
        return
    active = active_path.read_text(encoding="ascii").strip()
    if not active or "/" in active or "\\" in active:
        fail("credential active-key marker is invalid")
    key_ids = {p.stem for p in key_root.glob("*.key") if p.is_file()}
    for key_file in key_root.glob("*.key"):
        if key_file.stat().st_size != 32:
            fail(f"credential vault key has an invalid length: {key_file.name}")
    if active not in key_ids:
        fail("credential active-key marker references a missing key")
    db = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True, timeout=10)
    try:
        for table in ("user_credentials", "system_credentials"):
            exists = db.execute(
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (table,)
            ).fetchone()
            if not exists:
                continue
            for (value,) in db.execute(f"SELECT encrypted_value FROM {table}"):
                if not isinstance(value, str) or not value.startswith(prefix):
                    continue
                parts = value[len(prefix):].split(":", 1)
                if len(parts) != 2 or parts[0] not in key_ids:
                    fail("credential database references a missing vault key")
        rotation = db.execute(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='credential_key_rotation'"
        ).fetchone()
        if rotation:
            for target_key_id, status in db.execute(
                "SELECT target_key_id,status FROM credential_key_rotation WHERE status='running'"
            ):
                if status == "running" and target_key_id not in key_ids:
                    fail("credential rotation journal references a missing vault key")
    finally:
        db.close()


def validate_channel_rotation(data_root: Path) -> None:
    gateway_private = data_root / "security/channel/gateway-private"
    channel_trust = data_root / "security/channel/channel-trust"
    next_paths = (
        gateway_private / "gateway-channel-client.pfx.next",
        gateway_private / "gateway-channel-delegation.pfx.next",
        channel_trust / "gateway-channel-client.cer.next",
        channel_trust / "gateway-channel-delegation.cer.next",
    )
    previous_paths = (
        gateway_private / "gateway-channel-client.pfx.previous",
        gateway_private / "gateway-channel-delegation.pfx.previous",
        channel_trust / "gateway-channel-client.cer.previous",
        channel_trust / "gateway-channel-delegation.cer.previous",
    )
    for slot_name, slot in (("next", next_paths), ("previous", previous_paths)):
        present = [path.is_file() for path in slot]
        if any(present) and not all(present):
            fail(f"channel security {slot_name} rotation slot is incomplete")
    rotation_manifest = channel_trust / "channel-security-rotation.json"
    if rotation_manifest.is_file():
        if not all(path.is_file() for path in previous_paths):
            fail("channel security rotation manifest has no complete previous certificate slot")
        try:
            text = rotation_manifest.read_text(encoding="utf-8")
            fields = dict(line.split("=", 1) for line in text.splitlines() if "=" in line)
        except (OSError, UnicodeError, ValueError) as ex:
            fail(f"channel security rotation manifest is invalid: {ex}")
        if fields.get("version") != "1" or fields.get("phase") != "ACTIVE":
            fail("channel security rotation manifest has an unsupported state")


def set_metadata(path: Path, item: dict) -> None:
    if os.name != "nt":
        os.chown(path, int(item["uid"]), int(item["gid"]), follow_symlinks=False)
        os.chmod(path, int(item["mode"]), follow_symlinks=False)


def metadata(path: Path, logical: str, kind: str) -> dict:
    st = safe_regular(path)
    return {
        "path": logical,
        "kind": kind,
        "present": True,
        "mode": stat.S_IMODE(st.st_mode),
        "uid": st.st_uid,
        "gid": st.st_gid,
        "size": st.st_size,
        "sha256": digest(path),
    }


def capture_directory_metadata(root: Path, entries: list[dict]) -> list[dict]:
    required: set[str] = {"."}
    for item in entries:
        parts = Path(item["path"]).parts[:-1]
        for index in range(1, len(parts) + 1):
            required.add(Path(*parts[:index]).as_posix())
    result = []
    for relative in sorted(required, key=lambda value: (value.count("/"), value)):
        path = root if relative == "." else root / relative
        try:
            st = path.lstat()
        except FileNotFoundError:
            continue
        if not stat.S_ISDIR(st.st_mode):
            fail(f"unsupported symbolic or non-directory application-state ancestor: {path}")
        result.append({
            "path": relative,
            "mode": stat.S_IMODE(st.st_mode),
            "uid": st.st_uid,
            "gid": st.st_gid,
        })
    return result


def fixed_inventory() -> list[tuple[str, str]]:
    paths: list[tuple[str, str]] = [(name, "database") for name in DATABASES]
    paths.extend((name, "key") for name in ROOT_KEYS)
    for directory, names in SERVICE_KEYS.items():
        for name in names:
            paths.append(((Path(directory) / name).as_posix(), "key"))
            if name.endswith((".pfx", ".cer")):
                paths.append(((Path(directory) / (name + ".previous")).as_posix(), "key"))
    for directory, names in CHANNEL_SECURITY_FILES.items():
        for name in names:
            paths.append(((Path(directory) / name).as_posix(), "key"))
            if name.endswith((".pfx", ".cer")) and not name.endswith((".next", ".previous")):
                paths.append(((Path(directory) / (name + ".next")).as_posix(), "key"))
                paths.append(((Path(directory) / (name + ".previous")).as_posix(), "key"))
    # The active marker is part of the fixed schema even when it is absent. Its
    # explicit absent record prevents an incomplete manifest from silently
    # interpreting omission as permission to delete live state during restore.
    paths.append(("credentialsHost/vault/vault-keys/active", "key"))
    # Deduplicate while preserving deterministic inventory order.
    return list(dict.fromkeys(paths))


def inventory(data_root: Path) -> list[tuple[str, str]]:
    paths = fixed_inventory()
    for directory in CHANNEL_SECURITY_FILES:
        root = data_root / directory
        if not root.exists() and not root.is_symlink():
            continue
        if root.is_symlink() or not root.is_dir():
            fail(f"unsupported channel security material root: {root}")
        expected_names = {Path(rel).name for rel, _ in paths if Path(rel).parent.as_posix() == directory}
        for entry in root.iterdir():
            if entry.is_symlink() or not entry.is_file():
                fail(f"unsupported channel security material entry: {entry}")
            if entry.name not in expected_names:
                fail(f"unsupported channel security material file: {entry}")
    agent_root = data_root / "agents"
    if agent_root.exists() or agent_root.is_symlink():
        if agent_root.is_symlink() or not agent_root.is_dir():
            fail(f"unsupported agents state root: {agent_root}")
        for directory in sorted(agent_root.iterdir()):
            if directory.is_symlink() or not directory.is_dir() or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", directory.name):
                fail(f"unsupported agent state directory: {directory}")
            db = directory / "agent.db"
            if db.exists() or db.is_symlink():
                paths.append((db.relative_to(data_root).as_posix(), "database"))
    key_root = data_root / "credentialsHost" / "vault" / "vault-keys"
    if key_root.exists() or key_root.is_symlink():
        if key_root.is_symlink() or not key_root.is_dir():
            fail(f"unsupported credential key ring root: {key_root}")
        for key in sorted(key_root.iterdir()):
            if key.name == "active":
                continue
            if key.suffix == ".key":
                if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.key", key.name):
                    fail(f"unsupported credential key identifier: {key.name}")
                paths.append((key.relative_to(data_root).as_posix(), "key"))
            elif key.is_file() or key.is_symlink():
                fail(f"unsupported file in credential key ring: {key}")
    # The user keys are included only when they are regular files. A dangling
    # symlink or directory at one of these well-known paths is unsupported.
    for rel, _ in paths:
        candidate = data_root / rel
        parent = candidate.parent
        while parent != data_root:
            if parent.exists() or parent.is_symlink():
                parent_stat = parent.lstat()
                if stat.S_ISLNK(parent_stat.st_mode) or not stat.S_ISDIR(parent_stat.st_mode):
                    fail(f"unsupported symbolic or non-directory application-state parent: {parent}")
            parent = parent.parent
        if candidate.is_symlink():
            fail(f"symbolic links in supported application state are unsupported: {candidate}")
    # Deduplicate while preserving deterministic inventory order.
    return list(dict.fromkeys(paths))


def capture(args: argparse.Namespace) -> None:
    root = Path(args.data_root).resolve(strict=True)
    candidate = Path(args.destination)
    if candidate.exists():
        fail(f"candidate destination already exists: {candidate}")
    candidate.mkdir(parents=True, mode=0o700)
    try:
        payload = candidate / "state"
        payload.mkdir(mode=0o700)
        entries = []
        for rel, kind in inventory(root):
            src = root / rel
            if not src.exists():
                entries.append({"path": rel, "kind": kind, "present": False})
                continue
            safe_regular(src)
            dst = payload / rel
            dst.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            if kind == "database":
                source = sqlite3.connect(f"file:{src}?mode=ro", uri=True, timeout=10)
                target = sqlite3.connect(str(dst), timeout=10)
                try:
                    source.backup(target)
                    target.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                    target.commit()
                finally:
                    target.close()
                    source.close()
                db_integrity(dst)
            else:
                shutil.copyfile(src, dst, follow_symlinks=False)
            item = metadata(src, rel, kind)
            item["sourceSha256"] = item["sha256"]
            item["sha256"] = digest(dst)
            item["parentMode"] = stat.S_IMODE(src.parent.stat().st_mode)
            item["parentUid"] = src.parent.stat().st_uid
            item["parentGid"] = src.parent.stat().st_gid
            set_metadata(dst, item)
            fsync_file(dst)
            entries.append(item)
        validate_credential_material(payload, payload)
        validate_channel_rotation(payload)
        manifest = {
            "format": FORMAT,
            "capturedAtUtc": datetime.now(timezone.utc).isoformat(),
            "operationId": args.operation,
            "sourceRelease": args.source,
            "targetRelease": args.target,
            "dataRoot": str(root),
            "entries": entries,
            "directories": capture_directory_metadata(root, entries),
        }
        manifest_path = candidate / "manifest.json"
        with manifest_path.open("x", encoding="utf-8") as stream:
            json.dump(manifest, stream, sort_keys=True, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        fsync_dir(payload)
        fsync_dir(candidate)
        verify(args, candidate)
    except Exception:
        shutil.rmtree(candidate, ignore_errors=True)
        raise


def read_manifest(snapshot: Path, operation: str | None = None,
                  source: str | None = None, target: str | None = None) -> dict:
    try:
        snapshot_stat = snapshot.lstat()
    except FileNotFoundError:
        fail(f"snapshot directory is missing: {snapshot}")
    if not stat.S_ISDIR(snapshot_stat.st_mode):
        fail(f"snapshot path is not a real directory: {snapshot}")
    path = snapshot / "manifest.json"
    if path.is_symlink() or not path.is_file():
        fail(f"snapshot manifest missing or invalid: {path}")
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if manifest.get("format") != FORMAT:
        fail("unsupported snapshot format")
    for field, expected in (("operationId", operation), ("sourceRelease", source), ("targetRelease", target)):
        if expected is not None and manifest.get(field) != expected:
            fail(f"snapshot {field} does not match the active operation")
    if not isinstance(manifest.get("entries"), list):
        fail("snapshot inventory is invalid")
    return manifest


def verify(args: argparse.Namespace, snapshot: Path | None = None) -> None:
    path = snapshot or Path(args.snapshot)
    manifest_digest = getattr(args, "manifest_sha256", None)
    if manifest_digest is not None:
        if not re.fullmatch(r"[0-9a-f]{64}", manifest_digest):
            fail("snapshot manifest binding is invalid")
        if digest(path / "manifest.json") != manifest_digest:
            fail("snapshot manifest does not match its durable journal binding")
    manifest = read_manifest(path, getattr(args, "operation", None),
                             getattr(args, "source", None), getattr(args, "target", None))
    expected_root = Path(args.data_root).resolve(strict=True)
    if Path(manifest.get("dataRoot", "")).resolve() != expected_root:
        fail("snapshot data root does not match the supported configured data root")
    payload = path / "state"
    if payload.is_symlink() or not payload.is_dir():
        fail("snapshot state directory is missing or invalid")
    directories = manifest.get("directories")
    if not isinstance(directories, list):
        fail("snapshot directory metadata inventory is missing")
    directory_entries: dict[str, dict] = {}
    allowed_directories = {"agents"}
    for file_rel, _kind in fixed_inventory():
        parts = Path(file_rel).parts
        allowed_directories.update(Path(*parts[:depth]).as_posix() for depth in range(1, len(parts)))
    for item in directories:
        if not isinstance(item, dict):
            fail("snapshot contains malformed directory metadata")
        rel = item.get("path")
        if (not isinstance(rel, str) or rel in directory_entries or "\\" in rel
                or rel.startswith("/") or ".." in Path(rel).parts):
            fail("snapshot contains an invalid or duplicate directory metadata path")
        if rel != "." and rel not in allowed_directories \
                and not re.fullmatch(r"agents/[A-Za-z0-9][A-Za-z0-9._-]{0,127}", rel):
            fail(f"snapshot directory metadata is outside the supported inventory: {rel}")
        for field in ("mode", "uid", "gid"):
            if type(item.get(field)) is not int or item[field] < 0:
                fail(f"snapshot directory metadata is invalid for {rel}: {field}")
        directory_entries[rel] = item
    if "." not in directory_entries:
        fail("snapshot directory metadata omits the application data root")
    seen: dict[str, dict] = {}
    fixed = dict(fixed_inventory())
    dynamic_agent = re.compile(r"agents/([A-Za-z0-9][A-Za-z0-9._-]{0,127})/agent\.db\Z")
    dynamic_key = re.compile(r"credentialsHost/vault/vault-keys/([A-Za-z0-9][A-Za-z0-9._-]{0,127})\.key\Z")
    for item in manifest["entries"]:
        if not isinstance(item, dict):
            fail("snapshot contains a malformed inventory entry")
        rel = item.get("path")
        if not isinstance(rel, str) or rel.startswith("/") or "\\" in rel or ".." in Path(rel).parts or rel in seen:
            fail("snapshot contains an invalid or duplicate inventory path")
        seen[rel] = item
        kind = fixed.get(rel)
        dynamic = dynamic_agent.fullmatch(rel) is not None or dynamic_key.fullmatch(rel) is not None
        if kind is None and not dynamic:
            fail("snapshot contains a path outside the supported application-state inventory")
        expected_kind = kind or ("database" if dynamic_agent.fullmatch(rel) else "key")
        if item.get("kind") != expected_kind or type(item.get("present")) is not bool:
            fail(f"snapshot inventory kind or presence is invalid for {rel}")
        if dynamic and item["present"] is not True:
            fail(f"dynamic snapshot entry must contain captured data: {rel}")
        if not item["present"]:
            if set(item) != {"path", "kind", "present"}:
                fail(f"absent snapshot entry has unexpected content: {rel}")
            continue
        file = payload / rel
        cursor = file.parent
        while cursor != payload.parent:
            if cursor.is_symlink() or not cursor.is_dir():
                fail(f"snapshot contains an unsafe parent path for {rel}")
            if cursor == payload:
                break
            cursor = cursor.parent
        safe_regular(file)
        if not re.fullmatch(r"[0-9a-f]{64}", str(item.get("sha256", ""))):
            fail(f"snapshot hash is invalid for {rel}")
        for field in ("mode", "uid", "gid", "size", "parentMode", "parentUid", "parentGid"):
            if type(item.get(field)) is not int or item[field] < 0:
                fail(f"snapshot metadata is invalid for {rel}: {field}")
        parent = Path(rel).parent.as_posix()
        if parent not in directory_entries:
            fail(f"snapshot directory metadata omits the parent of {rel}")
        if digest(file) != item.get("sha256"):
            fail(f"snapshot data hash mismatch for {rel}")
        if expected_kind == "database":
            db_integrity(file)
    missing = sorted(set(fixed) - set(seen))
    if missing:
        fail(f"snapshot inventory is incomplete; missing required entries: {', '.join(missing[:5])}")
    validate_credential_material(payload, payload)
    validate_channel_rotation(payload)


def restore(args: argparse.Namespace) -> None:
    root = Path(args.data_root).resolve(strict=True)
    snapshot = Path(args.snapshot)
    manifest = read_manifest(snapshot, args.operation, args.source, args.target)
    if Path(manifest["dataRoot"]) != root:
        fail("snapshot data root does not match the configured application data root")
    verify(args, snapshot)
    payload = snapshot / "state"
    saved = {item["path"]: item for item in manifest["entries"]}
    if os.name == "nt":
        # Windows is used for pure unit tests; the Linux root helper is the
        # production caller and uses the dirfd-pinned implementation.
        current = inventory(root)
        for rel, kind in current:
            if rel not in saved or not saved[rel].get("present"):
                path = root / rel
                for suffix in ("", "-wal", "-shm", "-journal"):
                    candidate = Path(str(path) + suffix)
                    if candidate.is_symlink():
                        fail(f"refusing to remove unsupported symbolic link: {candidate}")
                    if candidate.is_file():
                        candidate.unlink()
        for rel, item in saved.items():
            if not item.get("present"):
                continue
            src = payload / rel
            dst = root / rel
            # The production helper is Linux-only. Keep the snapshot engine's
            # platform-neutral unit tests useful on Windows with exclusive
            # random staging; the dirfd/no-follow path below is the appliance
            # security boundary.
            dst.parent.mkdir(parents=True, exist_ok=True)
            temporary = dst.with_name(f".{dst.name}.restore-{secrets.token_hex(16)}")
            with src.open("rb") as source, temporary.open("xb") as target:
                shutil.copyfileobj(source, target)
                target.flush()
                os.fsync(target.fileno())
            try:
                os.replace(temporary, dst)
            finally:
                temporary.unlink(missing_ok=True)
            set_metadata(dst, item)
            if item["kind"] == "database":
                db_integrity(dst)
            if digest(dst) != item["sha256"]:
                fail(f"restored file hash mismatch for {rel}")
        fsync_dir(root)
        return

    lock_journal = Path(getattr(args, "lock_journal", None) or
                        (root.parent / f".{root.name}.recovery-lock.json"))
    lock = DestinationTreeLock(root, lock_journal, args.operation)
    restore_succeeded = False
    try:
        supported_directories = {"agents", "credentialsHost/vault/vault-keys"}
        for rel, _kind in fixed_inventory():
            parent = Path(rel).parent
            while parent != Path("."):
                supported_directories.add(parent.as_posix())
                parent = parent.parent
        lock.acquire([
            directory for directory in sorted(supported_directories)
            if (root / directory).exists() or (root / directory).is_symlink()
        ], directory_paths=True, create_missing=False)
        lock.acquire([
            item["path"] for item in manifest["entries"] if item["present"]
        ])
        lock.acquire([item["path"] for item in manifest["directories"]], directory_paths=True)
        for directory in manifest["directories"]:
            lock.set_final_metadata(directory["path"], directory["uid"], directory["gid"], directory["mode"])
        current = inventory(root)
        current_to_lock = []
        for rel, _kind in current:
            path = root / rel
            if any(Path(str(path) + suffix).exists() or Path(str(path) + suffix).is_symlink()
                   for suffix in ("", "-wal", "-shm", "-journal")):
                current_to_lock.append(rel)
        lock.acquire(current_to_lock, create_missing=False)
        for rel, kind in current:
            if rel not in saved or not saved[rel].get("present"):
                if not lock.has_parent(Path(rel)):
                    continue
                parent_fd = lock.parent_fd(Path(rel))
                name = Path(rel).name
                for suffix in ("", "-wal", "-shm", "-journal"):
                    candidate = name + suffix
                    try:
                        st = os.stat(candidate, dir_fd=parent_fd, follow_symlinks=False)
                    except FileNotFoundError:
                        continue
                    if not stat.S_ISREG(st.st_mode):
                        fail(f"refusing to remove unsupported non-regular application-state file: {root / rel}{suffix}")
                    os.unlink(candidate, dir_fd=parent_fd)
        for rel, item in saved.items():
            if not item.get("present"):
                continue
            src = payload / rel
            dst = root / rel
            parent_fd = lock.parent_fd(Path(rel))
            temporary = f".{Path(rel).name}.restore-{secrets.token_hex(16)}"
            temp_fd = -1
            try:
                name = Path(rel).name
                prefix = f".{name}.restore-"
                for stale_name in os.listdir(parent_fd):
                    if stale_name.startswith(prefix) and stale_name != temporary:
                        stale = os.stat(stale_name, dir_fd=parent_fd, follow_symlinks=False)
                        if stat.S_ISREG(stale.st_mode):
                            os.unlink(stale_name, dir_fd=parent_fd)
                try:
                    existing = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
                except FileNotFoundError:
                    existing = None
                if existing is not None and not stat.S_ISREG(existing.st_mode):
                    fail(f"refusing to replace unsupported non-regular destination: {dst}")
                for suffix in ("-wal", "-shm", "-journal"):
                    sidecar_name = name + suffix
                    try:
                        sidecar_stat = os.stat(sidecar_name, dir_fd=parent_fd, follow_symlinks=False)
                    except FileNotFoundError:
                        continue
                    if not stat.S_ISREG(sidecar_stat.st_mode):
                        fail(f"refusing to remove unsupported non-regular SQLite sidecar: {dst}{suffix}")
                    os.unlink(sidecar_name, dir_fd=parent_fd)

                # Exclusive, no-follow staging and dirfd replacement prevent
                # predictable-name symlink writes and path re-resolution races.
                temp_fd = os.open(
                    temporary,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=parent_fd,
                )
                source_fd = os.open(src, os.O_RDONLY | os.O_NOFOLLOW)
                try:
                    while True:
                        block = os.read(source_fd, 1024 * 1024)
                        if not block:
                            break
                        view = memoryview(block)
                        while view:
                            written = os.write(temp_fd, view)
                            view = view[written:]
                finally:
                    os.close(source_fd)
                os.fchown(temp_fd, int(item["uid"]), int(item["gid"]))
                os.fchmod(temp_fd, int(item["mode"]))
                os.fsync(temp_fd)
                os.close(temp_fd)
                temp_fd = -1
                try:
                    current_st = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
                except FileNotFoundError:
                    current_st = None
                if current_st is not None and not stat.S_ISREG(current_st.st_mode):
                    fail(f"destination changed to a non-regular path during restore: {dst}")
                os.replace(temporary, name, src_dir_fd=parent_fd, dst_dir_fd=parent_fd)
                os.fsync(parent_fd)
            finally:
                if temp_fd >= 0:
                    os.close(temp_fd)
                try:
                    os.unlink(temporary, dir_fd=parent_fd)
                except FileNotFoundError:
                    pass
            if item["kind"] == "database":
                db_integrity(dst)
            if digest(dst) != item["sha256"]:
                fail(f"restored file hash mismatch for {rel}")
        restore_succeeded = True
    finally:
        lock.close(restore_succeeded)
    fsync_dir(root)


class DestinationTreeLock:
    """Durable lock for live destination directory metadata during restore.

    Each inode's original metadata is fsynced to the root-owned side journal
    before that inode is changed. A crash leaves directories root-owned and
    inaccessible to the service account; the next restore validates inode
    identity, re-locks them, and finishes or retries the snapshot operation.
    """

    def __init__(self, root: Path, journal_path: Path, operation: str):
        self.root = root
        self.journal_path = journal_path
        self.operation = operation
        self.flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        self.fds: dict[str, int] = {}
        self.entries: dict[str, dict] = {}
        self.closed = False
        self.initialized = False
        self.privileged = os.geteuid() == 0

    def acquire(self, relatives: list[str], directory_paths: bool = False, create_missing: bool = True) -> None:
        self._initialize()
        for relative in relatives:
            parts = Path(relative).parts if directory_paths else Path(relative).parts[:-1]
            current = ""
            for component in parts:
                child_key = f"{current}/{component}".strip("/")
                if child_key not in self.fds:
                    if not self._open_directory(child_key, create_missing):
                        break
                current = child_key

    def _initialize(self) -> None:
        if self.initialized:
            return
        self.initialized = True
        existing = self._read_journal()
        self.entries = existing
        root_fd = os.open(self.root, self.flags)
        self.fds[""] = root_fd
        self._record_or_validate(".", root_fd, existing)
        self._secure(root_fd)
        for rel in sorted((key for key in existing if key != "."), key=lambda value: (value.count("/"), value)):
            if not self._open_directory(rel, create_missing=False):
                fail(f"durable restore metadata journal references a missing directory: {rel}")

    def _read_journal(self) -> dict[str, dict]:
        try:
            st = self.journal_path.lstat()
        except FileNotFoundError:
            return {}
        if not stat.S_ISREG(st.st_mode) or stat.S_ISLNK(st.st_mode):
            fail("durable restore metadata journal is not a regular file")
        if self.privileged and (st.st_uid != 0 or stat.S_IMODE(st.st_mode) & 0o022):
            fail("durable restore metadata journal must be root-owned and not writable by others")
        try:
            document = json.loads(self.journal_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            fail(f"durable restore metadata journal cannot be read: {exc}")
        if document.get("format") != 1 or document.get("operationId") != self.operation \
                or document.get("dataRoot") != str(self.root) or not isinstance(document.get("entries"), list):
            fail("durable restore metadata journal does not match this recovery operation")
        entries = {}
        for item in document["entries"]:
            if not isinstance(item, dict) or not isinstance(item.get("path"), str) or item["path"] in entries:
                fail("durable restore metadata journal contains invalid directory records")
            for field in ("device", "inode", "uid", "gid", "mode"):
                if type(item.get(field)) is not int or item[field] < 0:
                    fail(f"durable restore metadata journal has invalid {field}")
            entries[item["path"]] = item
        if "." not in entries:
            fail("durable restore metadata journal omits the application root")
        return entries

    def _persist(self) -> None:
        self.journal_path.parent.mkdir(parents=True, exist_ok=True)
        parent_stat = self.journal_path.parent.stat()
        if self.privileged and (parent_stat.st_uid != 0 or stat.S_IMODE(parent_stat.st_mode) & 0o022):
            fail("durable restore metadata journal directory must be root-owned and not writable by others")
        try:
            existing = self.journal_path.lstat()
        except FileNotFoundError:
            existing = None
        if existing is not None and (not stat.S_ISREG(existing.st_mode) or stat.S_ISLNK(existing.st_mode)):
            fail("refusing to replace an unsafe durable restore metadata journal")
        document = {
            "format": 1,
            "operationId": self.operation,
            "dataRoot": str(self.root),
            "entries": [self.entries[key] for key in sorted(self.entries, key=lambda value: (value.count("/"), value))],
        }
        fd, temporary = tempfile.mkstemp(prefix=".restore-metadata-", dir=self.journal_path.parent)
        try:
            if self.privileged:
                os.fchown(fd, 0, 0)
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8", closefd=True) as stream:
                json.dump(document, stream, sort_keys=True, separators=(",", ":"))
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.journal_path)
            fsync_dir(self.journal_path.parent)
        finally:
            try:
                os.unlink(temporary)
            except FileNotFoundError:
                pass

    def _record_or_validate(self, relative: str, fd: int, existing: dict[str, dict]) -> None:
        st = os.fstat(fd)
        record = existing.get(relative)
        if record is not None:
            if record["device"] != st.st_dev or record["inode"] != st.st_ino:
                fail(f"application-state directory identity changed during recovery: {relative}")
            self.entries[relative] = record
            return
        self.entries[relative] = {
            "path": relative,
            "device": st.st_dev,
            "inode": st.st_ino,
            "uid": st.st_uid,
            "gid": st.st_gid,
            "mode": stat.S_IMODE(st.st_mode),
        }
        self._persist()

    def _secure(self, fd: int) -> None:
        if not self.privileged:
            return
        # The original metadata is already durable before this mutation. Once
        # chowned, the service account can no longer chmod the directory back.
        os.fchmod(fd, 0)
        os.fchown(fd, 0, 0)
        os.fchmod(fd, 0o500)
        os.fsync(fd)

    def _open_directory(self, key: str, create_missing: bool) -> bool:
        parent_key, _, component = key.rpartition("/")
        parent_fd = self.fds[parent_key]
        try:
            fd = os.open(component, self.flags, dir_fd=parent_fd)
        except FileNotFoundError:
            if not create_missing:
                return False
            os.mkdir(component, 0o700, dir_fd=parent_fd)
            fd = os.open(component, self.flags, dir_fd=parent_fd)
        except OSError as exc:
            if exc.errno in (errno.ELOOP, errno.ENOTDIR):
                fail(f"refusing symlinked or non-directory destination ancestor: {self.root / key}")
            raise
        existing = self.entries
        self._record_or_validate(key, fd, existing)
        self._secure(fd)
        self.fds[key] = fd
        return True

    def has_parent(self, relative: Path) -> bool:
        parent = relative.parent.as_posix()
        return (parent if parent != "." else "") in self.fds

    def parent_fd(self, relative: Path) -> int:
        parent = relative.parent.as_posix()
        return self.fds[parent if parent != "." else ""]

    def set_final_metadata(self, relative: str, uid: int, gid: int, mode: int) -> None:
        key = "." if relative in ("", ".") else relative
        if key not in self.entries:
            fail(f"snapshot metadata names an unlocked application directory: {relative}")
        self.entries[key]["finalUid"] = uid
        self.entries[key]["finalGid"] = gid
        self.entries[key]["finalMode"] = mode
        self._persist()

    def close(self, success: bool) -> None:
        if self.closed:
            return
        self.closed = True
        failures = []
        if success:
            for key in sorted(self.fds, key=lambda value: (value.count("/"), value), reverse=True):
                fd = self.fds[key]
                entry = self.entries["." if key == "" else key]
                uid = entry.get("finalUid", entry["uid"])
                gid = entry.get("finalGid", entry["gid"])
                mode = entry.get("finalMode", entry["mode"])
                try:
                    os.fchown(fd, uid, gid)
                    os.fchmod(fd, mode)
                    os.fsync(fd)
                except OSError as exc:
                    failures.append(exc)
        for fd in self.fds.values():
            os.close(fd)
        if failures:
            fail(f"could not restore application-state directory metadata: {failures[0]}")
        if success:
            self.journal_path.unlink(missing_ok=True)
            fsync_dir(self.journal_path.parent)


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser()
    subs = p.add_subparsers(dest="command", required=True)
    for name in ("capture", "verify", "restore"):
        cmd = subs.add_parser(name)
        cmd.add_argument("--data-root", required=True)
        if name == "capture":
            cmd.add_argument("--destination", required=True)
            cmd.add_argument("--operation", required=True)
            cmd.add_argument("--source", required=True)
            cmd.add_argument("--target", required=True)
        else:
            cmd.add_argument("--snapshot", required=True)
            cmd.add_argument("--operation")
            cmd.add_argument("--source")
            cmd.add_argument("--target")
            cmd.add_argument("--manifest-sha256")
            if name == "restore":
                cmd.add_argument("--lock-journal")
    return p


def main() -> int:
    args = parser().parse_args()
    try:
        {"capture": capture, "verify": verify, "restore": restore}[args.command](args)
        print(f"{args.command} verified")
        return 0
    except (OSError, sqlite3.Error, ValueError, RuntimeError, json.JSONDecodeError) as ex:
        print(f"update recovery error: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
