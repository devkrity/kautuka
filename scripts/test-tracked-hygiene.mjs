#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const validator = path.join(root, "scripts", "validate-tracked-hygiene.mjs");
const temp = await mkdtemp(path.join(os.tmpdir(), "kautuka-tracked-hygiene-"));

function git(args, cwd) {
  const result = spawnSync("git", args, { cwd, encoding: "utf8" });
  if (result.status !== 0) throw new Error(`Git fixture setup failed: ${result.stderr || result.stdout}`);
}

function validate(cwd) {
  return spawnSync(process.execPath, [validator], { cwd, encoding: "utf8" });
}

try {
  const repository = path.join(temp, "repository");
  await mkdir(repository, { recursive: true });
  git(["init", "--quiet", "--initial-branch=main"], repository);
  await mkdir(path.join(repository, "tests", "fixtures", "protocol"), { recursive: true });
  await writeFile(path.join(repository, "tests", "fixtures", "protocol", "session.json"), '{"sessionId":"synthetic-fixture"}\n');
  await writeFile(path.join(repository, "src.cs"), "class Source {}\n");
  git(["add", "tests/fixtures/protocol/session.json", "src.cs"], repository);

  const clean = validate(repository);
  if (clean.status !== 0) throw new Error(`hygiene validator rejected ordinary source or a nested synthetic protocol fixture: ${clean.stderr}${clean.stdout}`);

  const forbiddenPaths = [
    "kautuka.db",
    "kautuka.db-wal",
    "kautuka.db-shm",
    "kautuka.db.bak-20261029",
    "data/runtime.sqlite",
    "data/runtime.sqlite-wal",
    "data/runtime.sqlite-shm",
    "data/runtime.sqlite.bak",
    "config.json",
    "session.json",
  ];
  for (const relative of forbiddenPaths) {
    const target = path.join(repository, relative);
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, "synthetic runtime data fixture\n");
    git(["add", "-f", relative], repository);
  }

  const dirty = validate(repository);
  const output = `${dirty.stderr}${dirty.stdout}`;
  if (dirty.status === 0) throw new Error("hygiene validator accepted force-added runtime data and configuration paths");
  for (const relative of forbiddenPaths) {
    if (!output.includes(`- ${relative}`)) throw new Error(`hygiene validator did not report force-added path ${relative}`);
  }
  if (!output.includes("Tracked hygiene ratchet failed")) throw new Error("hygiene validator failed without identifying the policy");
  console.log("tracked hygiene passes normal source and nested synthetic session fixtures, and rejects force-added database, WAL/SHM, backup, root data, config, and session paths");
} finally {
  await rm(temp, { recursive: true, force: true });
}
