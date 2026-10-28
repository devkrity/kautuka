import { type ClassValue, clsx } from 'clsx';
import { twMerge } from 'tailwind-merge';

type CryptoLike = {
  randomUUID?: () => string;
  getRandomValues?: (array: Uint8Array) => Uint8Array;
};

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

const HAS_TIMEZONE_SUFFIX = /(Z|[+-]\d{2}:\d{2})$/i;

export function parseApiTimestamp(value: string): Date {
  const raw = String(value || '').trim();
  if (!raw || !HAS_TIMEZONE_SUFFIX.test(raw)) return new Date(0);

  const parsed = new Date(raw);
  if (Number.isNaN(parsed.getTime())) {
    return new Date(0);
  }
  return parsed;
}

export function createClientUuid(
  cryptoImpl: CryptoLike | undefined = globalThis.crypto as CryptoLike | undefined
): string {
  if (typeof cryptoImpl?.randomUUID === 'function') {
    return cryptoImpl.randomUUID();
  }

  const bytes = new Uint8Array(16);
  if (typeof cryptoImpl?.getRandomValues === 'function') {
    cryptoImpl.getRandomValues(bytes);
  } else {
    for (let index = 0; index < bytes.length; index += 1) {
      bytes[index] = Math.floor(Math.random() * 256);
    }
  }

  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;

  const hex = Array.from(bytes, (value) => value.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
