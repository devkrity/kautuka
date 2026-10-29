import { parseNaradPayload, type NaradRecord, type NaradSseFrame } from './narad-types.js';

export function parseSseFrames(sse: string): readonly NaradSseFrame[] {
  return sse.split(/\r?\n\r?\n/).flatMap((block) => {
    const lines = block.split(/\r?\n/);
    const data = lines.filter((line) => line.startsWith('data:')).map((line) => line.slice(5).trim()).join('');
    if (!data) return [];
    return [{ event: lines.find((line) => line.startsWith('event:'))?.slice(6).trim(), id: lines.find((line) => line.startsWith('id:'))?.slice(3).trim(), data }];
  });
}

export function parseSseRecords(sse: string): readonly NaradRecord[] {
  return parseSseFrames(sse).flatMap((frame) => {
    const record = parseNaradPayload(frame.data);
    return record ? [record] : [];
  });
}

export function naradEndpointKind(pathname: string): 'stream' | 'watch' | 'workspace-watch' | 'events' | undefined {
  if (/\/sessions\/[^/]+\/stream$/.test(pathname)) return 'stream';
  if (/\/sessions\/[^/]+\/watch$/.test(pathname)) return 'watch';
  if (/\/workspace\/watch$/.test(pathname)) return 'workspace-watch';
  if (/\/sessions\/[^/]+\/events$/.test(pathname)) return 'events';
  return undefined;
}
