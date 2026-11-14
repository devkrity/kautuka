import assert from "node:assert/strict";
import { chmodSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { webToolsMcpbCommand } from "./launcher-web-tools.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function storedZip(name, content) {
  const filename = Buffer.from(name);
  const payload = Buffer.from(content);
  const crc = crc32(payload);
  const local = Buffer.alloc(30 + filename.length + payload.length);
  local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt32LE(crc, 14);
  local.writeUInt32LE(payload.length, 18); local.writeUInt32LE(payload.length, 22);
  local.writeUInt16LE(filename.length, 26); filename.copy(local, 30); payload.copy(local, 30 + filename.length);
  const central = Buffer.alloc(46 + filename.length);
  central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6);
  central.writeUInt32LE(crc, 16); central.writeUInt32LE(payload.length, 20); central.writeUInt32LE(payload.length, 24);
  central.writeUInt16LE(filename.length, 28); filename.copy(central, 46);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length, 12); end.writeUInt32LE(local.length, 16);
  return Buffer.concat([local, central, end]);
}
const windows = webToolsMcpbCommand("win32", repositoryRoot);
const linux = webToolsMcpbCommand("linux", repositoryRoot);
const mac = webToolsMcpbCommand("darwin", repositoryRoot);

// Fail-first regression: every fresh launcher platform selects the existing native
// MCPB builder, which emits the versioned artifact discovered by Gateway.
assert.ok(windows.command === "pwsh" || windows.command === "powershell.exe");
assert.ok(windows.args.includes("-ExecutionPolicy"));
assert.equal(path.basename(windows.args.at(-1)), "build-mcpb.ps1");
assert.equal(linux.command, "bash");
assert.equal(path.basename(linux.args[0]), "build-mcpb.sh");
assert.deepEqual(mac, linux);
for (const command of [windows, linux]) assert.ok(existsSync(command.args.at(-1)));
for (const command of [windows, linux]) {
  const script = readFileSync(command.args.at(-1), "utf8");
  assert.match(script, /kautuka-web-tools-\$version\.mcpb/);
}

// Negative control: unsupported platforms fail explicitly instead of skipping staging.
assert.throws(() => webToolsMcpbCommand("freebsd", repositoryRoot), /staging is unsupported/);

if (process.platform === "linux" || process.platform === "win32") {
  const temporaryRoot = mkdtempSync(path.join(os.tmpdir(), "kautuka-web-tools-stage-test-"));
  try {
    const scriptDirectory = path.join(temporaryRoot, "mcps", "kautuka-web-tools");
    const scriptsDirectory = path.join(temporaryRoot, "scripts");
    const artifactDirectory = path.join(temporaryRoot, "artifacts", "mcpb");
    const fakeBin = path.join(temporaryRoot, "fake-bin");
    for (const directory of [scriptDirectory, scriptsDirectory, artifactDirectory, fakeBin]) mkdirSync(directory, { recursive: true });
    copyFileSync(path.join(repositoryRoot, "mcps/kautuka-web-tools/build-mcpb.sh"), path.join(scriptDirectory, "build-mcpb.sh"));
    copyFileSync(path.join(repositoryRoot, "mcps/kautuka-web-tools/build-mcpb.ps1"), path.join(scriptDirectory, "build-mcpb.ps1"));
    copyFileSync(path.join(repositoryRoot, "scripts/validate-mcpb-archive.mjs"), path.join(scriptsDirectory, "validate-mcpb-archive.mjs"));
    writeFileSync(path.join(scriptDirectory, "pyproject.toml"), '[project]\nversion = "0.1.0"\n');
    const fakeNpx = path.join(fakeBin, "npx");
    const validZip = path.join(temporaryRoot, "valid.zip");
    const corruptZip = path.join(temporaryRoot, "corrupt.zip");
    writeFileSync(validZip, storedZip("manifest.json", "{}"));
    const corrupt = storedZip("manifest.json", "{}"); corrupt[43] ^= 0xff;
    writeFileSync(corruptZip, corrupt);
    if (process.platform === "win32") {
      writeFileSync(`${fakeNpx}.cmd`, '@echo off\r\nif /I "%3"=="validate" exit /b 0\r\nif /I "%MCPB_MODE%"=="fail" exit /b 9\r\nif /I "%MCPB_MODE%"=="empty" exit /b 0\r\nif /I "%MCPB_MODE%"=="corrupt" copy /y "%CORRUPT_ZIP%" "%~5" >nul\r\nif /I "%MCPB_MODE%"=="corrupt" exit /b 0\r\ncopy /y "%VALID_ZIP%" "%~5" >nul\r\nexit /b %errorlevel%\r\n');
    } else {
      writeFileSync(fakeNpx, '#!/usr/bin/env bash\nset -e\n[[ "$3" == validate ]] && exit 0\n[[ "$3" == pack ]] || exit 2\ncase "$MCPB_MODE" in fail) exit 9 ;; empty) exit 0 ;; corrupt) cp "$CORRUPT_ZIP" "${@: -1}" ;; success) cp "$VALID_ZIP" "${@: -1}" ;; esac\n');
      chmodSync(fakeNpx, 0o755);
    }
    const artifact = path.join(artifactDirectory, "kautuka-web-tools-0.1.0.mcpb");
    const prior = Buffer.from("prior-known-good-bundle");
    writeFileSync(artifact, prior);
    const script = path.join(scriptDirectory, process.platform === "win32" ? "build-mcpb.ps1" : "build-mcpb.sh");
    const run = (mode) => process.platform === "win32"
      ? spawnSync(windows.command, [...windows.args.slice(0, -1), script], { cwd: temporaryRoot, encoding: "utf8", env: { ...process.env, PATH: `${fakeBin};${process.env.PATH}`, MCPB_MODE: mode, VALID_ZIP: validZip, CORRUPT_ZIP: corruptZip } })
      : spawnSync("bash", [script], { cwd: temporaryRoot, encoding: "utf8", env: { ...process.env, PATH: `${fakeBin}:${process.env.PATH}`, MCPB_MODE: mode, VALID_ZIP: validZip, CORRUPT_ZIP: corruptZip } });
    for (const mode of ["fail", "empty", "corrupt"]) {
      assert.notEqual(run(mode).status, 0, `${mode} package result must fail`);
      assert.deepEqual(readFileSync(artifact), prior, `${mode} package result retains previous artifact`);
    }
    const success = run("success");
    assert.equal(success.status, 0, `${success.stdout}\n${success.stderr}`);
    assert.deepEqual(readFileSync(artifact), readFileSync(validZip));
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true });
  }
}
process.stdout.write("Development web-tools MCPB staging checks passed.\n");
