import { createIdentityV3Hash, isValidPinShape, parseIdentityV3Hash, verifyIdentityV3Hash } from './pos-pin';

// Fixtures produced independently with .NET (System.Security.Cryptography.Rfc2898DeriveBytes, the same PBKDF2 the
// ASP.NET Identity v3 PasswordHasher uses), salt = bytes 1..16, 32-byte subkey, laid out as
// 0x01 | prf (UInt32 BE) | iterCount (UInt32 BE) | saltLen (UInt32 BE) | salt | subkey:
//   PIN "1234",   HMACSHA256 (prf 1),  10 000 iterations
//   PIN "482915", HMACSHA512 (prf 2), 100 000 iterations (the .NET 8 Identity default)
const DOTNET_SHA256_1234 = 'AQAAAAEAACcQAAAAEAECAwQFBgcICQoLDA0ODxCz5fFYrMQyZWFCAb9ADx7B9alHnGyEA1oec5MKLwiKBg==';
const DOTNET_SHA512_482915 = 'AQAAAAIAAYagAAAAEAECAwQFBgcICQoLDA0ODxBFiNwRFSMKFKeusBx2LMwxrhmeNZEB5kNNtSBPcG3XbA==';

describe('A38 offline PIN check (ASP.NET Identity v3 hash, WebCrypto PBKDF2)', () => {
    it('parses the v3 header', () => {
        const p = parseIdentityV3Hash(DOTNET_SHA512_482915)!;
        expect(p.hash).toBe('SHA-512');
        expect(p.iterations).toBe(100000);
        expect(Array.from(p.salt)).toEqual(Array.from({ length: 16 }, (_, i) => i + 1));
        expect(p.subkey.length).toBe(32);
    });

    it('verifies the .NET-generated HMACSHA256 hash', async () => {
        expect(await verifyIdentityV3Hash(DOTNET_SHA256_1234, '1234')).toBeTrue();
        expect(await verifyIdentityV3Hash(DOTNET_SHA256_1234, '1235')).toBeFalse();
    });

    it('verifies the .NET-generated HMACSHA512 hash (100k iterations)', async () => {
        expect(await verifyIdentityV3Hash(DOTNET_SHA512_482915, '482915')).toBeTrue();
        expect(await verifyIdentityV3Hash(DOTNET_SHA512_482915, '482916')).toBeFalse();
    });

    it('produces the same bytes as .NET when hashing in-browser', async () => {
        const salt = Uint8Array.from({ length: 16 }, (_, i) => i + 1);
        expect(await createIdentityV3Hash('1234', salt, 10000, 1)).toBe(DOTNET_SHA256_1234);
    });

    it('never matches malformed, v2 or empty hashes', async () => {
        expect(await verifyIdentityV3Hash('', '1234')).toBeFalse();
        expect(await verifyIdentityV3Hash(null, '1234')).toBeFalse();
        expect(await verifyIdentityV3Hash('not base64 !!', '1234')).toBeFalse();
        expect(await verifyIdentityV3Hash('AAECAwQ=', '1234')).toBeFalse(); // v2 marker 0x00
        expect(await verifyIdentityV3Hash(DOTNET_SHA256_1234, '')).toBeFalse();
    });

    it('accepts 4–6 digit PINs only', () => {
        expect(isValidPinShape('1234')).toBeTrue();
        expect(isValidPinShape('123456')).toBeTrue();
        expect(isValidPinShape('123')).toBeFalse();
        expect(isValidPinShape('1234567')).toBeFalse();
        expect(isValidPinShape('12a4')).toBeFalse();
    });
});
