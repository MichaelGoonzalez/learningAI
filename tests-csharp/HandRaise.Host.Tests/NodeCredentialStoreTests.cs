using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using HandRaise.Host.Services;

namespace HandRaise.Host.Tests;

public sealed class NodeCredentialStoreTests
{
    [Fact]
    public async Task GetOrCreateApiKeyAsync_WhenNoKeyExists_GeneratesCryptographicallySecureKeyAndPersists()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"node-cred-test-{Guid.NewGuid():N}.json");
        var protector = new FakeProtector();
        var store = new JsonNodeCredentialStore(tempFile, protector);

        try
        {
            var key = await store.GetOrCreateApiKeyAsync();

            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(key.Length >= 16);
            Assert.True(File.Exists(tempFile));

            // Verify it was stored encrypted
            var json = await File.ReadAllTextAsync(tempFile);
            Assert.DoesNotContain(key, json); // Key in plaintext must NOT exist in the JSON file
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetOrCreateApiKeyAsync_WhenKeyAlreadyExists_ReturnsSameKeyWithoutRegenerating()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"node-cred-test-{Guid.NewGuid():N}.json");
        var protector = new FakeProtector();
        var store = new JsonNodeCredentialStore(tempFile, protector);

        try
        {
            var key1 = await store.GetOrCreateApiKeyAsync();
            var key2 = await store.GetOrCreateApiKeyAsync();

            Assert.Equal(key1, key2);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RegenerateApiKeyAsync_GeneratesNewKeyAndOverwritesPrevious()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"node-cred-test-{Guid.NewGuid():N}.json");
        var protector = new FakeProtector();
        var store = new JsonNodeCredentialStore(tempFile, protector);

        try
        {
            var key1 = await store.GetOrCreateApiKeyAsync();
            var key2 = await store.RegenerateApiKeyAsync();

            Assert.NotEqual(key1, key2);
            Assert.False(string.IsNullOrWhiteSpace(key2));

            var reloaded = await store.GetApiKeyAsync();
            Assert.Equal(key2, reloaded);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetOrCreateApiKeyAsync_WhenTrivialKeyStored_ReplacesWithSecureKey()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"node-cred-test-{Guid.NewGuid():N}.json");
        var protector = new FakeProtector();
        var store = new JsonNodeCredentialStore(tempFile, protector);

        try
        {
            // Manually write "secret" to store
            await store.SaveApiKeyAsync("secret");

            // GetOrCreate should reject trivial key and generate a secure one
            var secureKey = await store.GetOrCreateApiKeyAsync();
            Assert.NotEqual("secret", secureKey);
            Assert.True(secureKey.Length >= 16);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private sealed class FakeProtector : IDataProtector
    {
        public byte[] Protect(byte[] value) => value.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] value) => value.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
