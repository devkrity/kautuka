// Display guidance and destination classification. Manager and the native boundary
// enforce the actual resolved destination, including hostnames whose DNS answers change.
export function destinationScope(
  destination: string
): 'metadata' | 'private' | 'public' | 'blocked' {
  if (!destination || typeof destination !== 'string') return 'blocked';
  const host = destination.toLowerCase().trim().replace(/\.$/, '');
  if (!host) return 'blocked';

  if (
    [
      '169.254.169.254',
      '169.254.170.2',
      '100.100.100.200',
      'metadata.google.internal',
      'instance-data',
    ].includes(host)
  ) {
    return 'metadata';
  }

  const parts = host.split('.').map(Number);
  if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) {
    // Hostname
    if (host.endsWith('.internal') || host.endsWith('.local') || host.endsWith('.localhost')) {
      return 'private';
    }
    return 'public';
  }

  const [a = 0, b = 0, c = 0] = parts;
  if (['192.0.0.9', '192.0.0.10'].includes(host)) return 'public';
  if (a === 169 && b === 254 && c === 170) return 'metadata';
  if (host === '192.0.0.8' || host === '168.63.129.16' || a === 0 || a === 127 || a >= 224) {
    return 'blocked';
  }

  // RFC1918 + RFC6598 + other special ranges
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

export function isIpAddress(destination: string): boolean {
  if (!destination) return false;
  const parts = destination.trim().split('.');
  return (
    parts.length === 4 &&
    parts.every((p) => {
      const n = Number(p);
      return Number.isInteger(n) && n >= 0 && n <= 255 && String(n) === p;
    })
  );
}

export function formatDestinationDisplay(destination: string, port?: number): string {
  const isIp = isIpAddress(destination);
  const destText = isIp ? `a server at ${destination}` : destination;
  if (port && port !== 443) {
    return `${destText}:${port}`;
  }
  return destText;
}
