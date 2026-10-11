// A38 API-CONTRACT §7 / D-4 — offline PIN check against the cached ASP.NET Identity v3 hashes.
// Format (base64): 0x01 | prf (UInt32 BE: 0 = HMACSHA1, 1 = HMACSHA256, 2 = HMACSHA512) | iterCount (UInt32 BE)
//                  | saltLength (UInt32 BE) | salt | subkey.

const PRF_HASH: Record<number, string> = { 0: 'SHA-1', 1: 'SHA-256', 2: 'SHA-512' };

export interface ParsedIdentityHash {
    hash: string;
    iterations: number;
    salt: Uint8Array;
    subkey: Uint8Array;
}

function base64ToBytes(b64: string): Uint8Array {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

function readUInt32BE(b: Uint8Array, offset: number): number {
    return ((b[offset] << 24) >>> 0) + (b[offset + 1] << 16) + (b[offset + 2] << 8) + b[offset + 3];
}

/** Parses an Identity v3 hash; null for any other format (v2, malformed, empty). */
export function parseIdentityV3Hash(b64: string | null | undefined): ParsedIdentityHash | null {
    if (!b64) return null;
    let bytes: Uint8Array;
    try {
        bytes = base64ToBytes(b64);
    } catch {
        return null;
    }
    if (bytes.length < 13 || bytes[0] !== 0x01) return null;
    const prf = readUInt32BE(bytes, 1);
    const iterations = readUInt32BE(bytes, 5);
    const saltLength = readUInt32BE(bytes, 9);
    const hash = PRF_HASH[prf];
    if (!hash || iterations < 1 || saltLength < 8 || 13 + saltLength >= bytes.length) return null;
    const salt = bytes.slice(13, 13 + saltLength);
    const subkey = bytes.slice(13 + saltLength);
    if (subkey.length < 16) return null;
    return { hash, iterations, salt, subkey };
}

/** Constant-time comparison. */
function equalBytes(a: Uint8Array, b: Uint8Array): boolean {
    if (a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
    return diff === 0;
}

/** PBKDF2 with WebCrypto (`crypto.subtle`, available on https and localhost). */
export async function pbkdf2(password: string, salt: Uint8Array, iterations: number, hash: string, lengthBytes: number): Promise<Uint8Array> {
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(password), 'PBKDF2', false, ['deriveBits']);
    const bits = await crypto.subtle.deriveBits({ name: 'PBKDF2', salt, iterations, hash }, key, lengthBytes * 8);
    return new Uint8Array(bits);
}

/** True when `pin` matches the Identity v3 `hashB64`. Unknown formats never match. */
export async function verifyIdentityV3Hash(hashB64: string | null | undefined, pin: string): Promise<boolean> {
    const parsed = parseIdentityV3Hash(hashB64);
    if (!parsed || !pin) return false;
    try {
        const derived = await pbkdf2(pin, parsed.salt, parsed.iterations, parsed.hash, parsed.subkey.length);
        return equalBytes(derived, parsed.subkey);
    } catch {
        return false;
    }
}

/** Builds an Identity v3 hash (used by specs; the server is the only producer in production). */
export async function createIdentityV3Hash(pin: string, salt: Uint8Array, iterations = 10_000, prf: 1 | 2 = 1): Promise<string> {
    const subkey = await pbkdf2(pin, salt, iterations, PRF_HASH[prf], 32);
    const out = new Uint8Array(13 + salt.length + subkey.length);
    const view = new DataView(out.buffer);
    out[0] = 0x01;
    view.setUint32(1, prf);
    view.setUint32(5, iterations);
    view.setUint32(9, salt.length);
    out.set(salt, 13);
    out.set(subkey, 13 + salt.length);
    let bin = '';
    out.forEach(b => (bin += String.fromCharCode(b)));
    return btoa(bin);
}

/** PIN shape (§2: 4–6 digits). */
export function isValidPinShape(pin: string): boolean {
    return /^\d{4,6}$/.test(pin);
}
