using System.Security.Cryptography;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Deedbox.Testing;
using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.PersonalData;

public sealed class PostgresKeyProviderTests(Databases databases) : KeyProviderTests(databases, Db.Postgres);

public sealed class SqlServerKeyProviderTests(Databases databases) : KeyProviderTests(databases, Db.SqlServer);

/// <summary>Every built-in key mode passes the same compliance suite.</summary>
public abstract class KeyProviderTests(Databases databases, Db db) : DatabaseTest(databases, db)
{
    [Fact]
    public async Task The_database_master_key_passes_compliance()
    {
        await using var provider = CreateProvider();
        await SchemaManager.Apply(provider, Ct);

        await KeyProviderCompliance.VerifyAsync(new DatabaseMasterKey(provider), Ct);
    }
}

public sealed class KeyRingAndAzureProviderTests
{
    [Fact]
    public async Task The_environment_key_ring_passes_compliance_and_reads_older_versions()
    {
        var old = new KeyRingMasterKey(Keys.Ring("ring-a"));
        var wrapped = await old.WrapAsync(RandomNumberGenerator.GetBytes(32), TestContext.Current.CancellationToken);
        var ring = new KeyRingMasterKey(Keys.Ring("ring-b", "ring-a"));

        await KeyProviderCompliance.VerifyAsync(ring, TestContext.Current.CancellationToken);
        Assert.Equal("env:ring-b", ring.KeyVersion);
        Assert.Equal(32, (await ring.UnwrapAsync(wrapped, "env:ring-a", TestContext.Current.CancellationToken)).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("v1:c2hvcnQ=")]
    public void An_invalid_key_ring_fails(string ring)
    {
        var error = Assert.Throws<DeedboxException>(() => new KeysBuilder().FromKeyRing(ring).Factory!(null!));

        Assert.Equal("DBX029", error.Code);
    }

    [Fact]
    public void FromEnvironment_fails_when_the_variable_is_missing_and_reads_it_when_set()
    {
        var name = "DEEDBOX_TEST_KEY_" + Guid.NewGuid().ToString("N");

        Assert.Equal("DBX029", Assert.Throws<DeedboxException>(() => new KeysBuilder().FromEnvironment(name)).Code);

        Environment.SetEnvironmentVariable(name, Keys.Ring("from-env"));
        try
        {
            Assert.Equal("env:from-env", new KeysBuilder().FromEnvironment(name).Factory!(null!).KeyVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task The_azure_key_vault_provider_passes_compliance_with_a_local_key()
    {
        using var rsa = RSA.Create(2048);
        var key = new JsonWebKey(rsa, includePrivateParameters: true, [KeyOperation.WrapKey, KeyOperation.UnwrapKey]) { Id = "https://vault.example/keys/deedbox/v1" };
        var keys = new KeysBuilder().UseAzureKeyVault(new CryptographyClient(key));

        var provider = keys.Factory!(null!);
        await KeyProviderCompliance.VerifyAsync(provider, TestContext.Current.CancellationToken);

        Assert.StartsWith("azure:https://vault.example/keys/deedbox", provider.KeyVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void The_azure_key_vault_provider_names_the_configured_key_before_its_first_wrap()
    {
        var keys = new KeysBuilder().UseAzureKeyVault(new Uri("https://vault.example/keys/deedbox/abc123"), new NoCredential());

        Assert.Equal("azure:https://vault.example/keys/deedbox/abc123", keys.Factory!(null!).KeyVersion);
    }

    [Fact]
    public async Task A_provider_that_does_not_verify_what_it_unwraps_fails_compliance()
    {
        var error = await Assert.ThrowsAsync<KeyProviderComplianceException>(() => KeyProviderCompliance.VerifyAsync(new Careless(), TestContext.Current.CancellationToken));

        Assert.Contains("altered bytes", error.Message, StringComparison.Ordinal);
    }

    private sealed class NoCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network in this test.");

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network in this test.");
    }

    private sealed class Careless : IMasterKeyProvider
    {
        public string KeyVersion => "careless:1";

        public Task<byte[]> WrapAsync(byte[] key, CancellationToken ct) => Task.FromResult(key.Select(b => (byte)(b ^ 0x5A)).ToArray());

        public Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct) =>
            keyVersion == KeyVersion ? Task.FromResult(wrappedKey.Select(b => (byte)(b ^ 0x5A)).ToArray()) : throw new CryptographicException("unknown version");
    }
}
