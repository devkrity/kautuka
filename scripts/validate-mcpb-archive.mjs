#!/usr/bin/env node
import { readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { inflateRawSync } from "node:zlib";

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

export function validateZipArchive(bytes) {
  const eocdMin = Math.max(0, bytes.length - 22 - 0xffff);
  let eocd = -1;
  for (let offset = bytes.length - 22; offset >= eocdMin; offset--) {
    if (bytes.readUInt32LE(offset) === 0x06054b50) { eocd = offset; break; }
  }
  if (eocd < 0) throw new Error("MCPB output has no ZIP end-of-central-directory record.");
  const entryCount = bytes.readUInt16LE(eocd + 10);
  const directorySize = bytes.readUInt32LE(eocd + 12);
  let cursor = bytes.readUInt32LE(eocd + 16);
  if (cursor + directorySize > eocd || entryCount === 0) throw new Error("MCPB output has an invalid ZIP directory.");
  let verifiedEntries = 0;
  while (cursor < bytes.readUInt32LE(eocd + 16) + directorySize) {
    if (bytes.readUInt32LE(cursor) !== 0x02014b50) throw new Error("MCPB output has a malformed ZIP directory entry.");
    const method = bytes.readUInt16LE(cursor + 10);
    const expectedCrc = bytes.readUInt32LE(cursor + 16);
    const compressedSize = bytes.readUInt32LE(cursor + 20);
    const expandedSize = bytes.readUInt32LE(cursor + 24);
    const nameLength = bytes.readUInt16LE(cursor + 28);
    const extraLength = bytes.readUInt16LE(cursor + 30);
    const commentLength = bytes.readUInt16LE(cursor + 32);
    const localOffset = bytes.readUInt32LE(cursor + 42);
    const name = bytes.subarray(cursor + 46, cursor + 46 + nameLength).toString("utf8");
    if (bytes.readUInt32LE(localOffset) !== 0x04034b50) throw new Error(`MCPB output has a missing local header for ${name}.`);
    const localNameLength = bytes.readUInt16LE(localOffset + 26);
    const localExtraLength = bytes.readUInt16LE(localOffset + 28);
    const dataStart = localOffset + 30 + localNameLength + localExtraLength;
    const compressed = bytes.subarray(dataStart, dataStart + compressedSize);
    if (compressed.length !== compressedSize) throw new Error(`MCPB output truncates ${name}.`);
    const expanded = method === 0 ? compressed : method === 8 ? inflateRawSync(compressed) : null;
    if (!expanded || expanded.length !== expandedSize || crc32(expanded) !== expectedCrc) throw new Error(`MCPB output has invalid compressed data for ${name}.`);
    cursor += 46 + nameLength + extraLength + commentLength;
    verifiedEntries++;
  }
  if (verifiedEntries !== entryCount) throw new Error("MCPB output ZIP entry count is inconsistent.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    validateZipArchive(readFileSync(process.argv[2]));
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exit(1);
  }
}
