// Display guidance only. Manager and the native boundary enforce the actual
// resolved destination, including hostnames whose DNS answers change.
export function destinationScope(
  destination: string
): 'metadata' | 'private' | 'public' | 'blocked' {
  const host = destination.toLowerCase().replace(/\.$/, '');
  if (
    ['169.254.169.254', '169.254.170.2', '100.100.100.200', 'metadata.google.internal'].includes(
      host
    )
  )
    return 'metadata';
  const parts = host.split('.').map(Number);
  if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255))
    return 'public';
  const [a = 0, b = 0, c = 0] = parts;
  if (['192.0.0.9', '192.0.0.10'].includes(host)) return 'public';
  if (a === 169 && b === 254 && c === 170) return 'metadata';
  if (host === '192.0.0.8' || host === '168.63.129.16' || a === 0 || a === 127 || a >= 224)
    return 'blocked';
  return a === 10 ||
    (a === 169 && b === 254) ||
    (a === 172 && b >= 16 && b <= 31) ||
    (a === 192 && b === 168) ||
    (a === 192 && b === 0 && [0, 2].includes(c)) ||
    (a === 192 && b === 88 && c === 99) ||
    (a === 198 && [18, 19].includes(b)) ||
    (a === 198 && b === 51 && c === 100) ||
    (a === 203 && b === 0 && c === 113) ||
    (a === 100 && b >= 64 && b <= 127)
    ? 'private'
    : 'public';
}
