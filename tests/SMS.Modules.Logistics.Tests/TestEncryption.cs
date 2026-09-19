using Microsoft.Extensions.Options;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Tests;

// The real AesEncryptionService, keyed for tests.
//
// Deliberately not a fake. A pass-through double would let a vault that forgot to encrypt pass
// every test, and could never show that tampering or a changed key is detected — the two failures
// the authenticated format exists to catch.
internal static class TestEncryption
{
    internal const string DefaultKey = "logistics-tests-encryption-key-0001";

    internal static AesEncryptionService New(string key = DefaultKey) =>
        new(Options.Create(new AppSettings { AesEncryptionKey = key }));
}
