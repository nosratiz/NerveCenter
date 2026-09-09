using System.Security.Cryptography;
using FluentAssertions;
using SbConsole.Core.Security;

namespace SbConsole.Core.Tests.Security;

public class AesGcmSecretProtectorTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Roundtrip_returns_original_plaintext()
    {
        var protector = new AesGcmSecretProtector(Key);

        var blob = protector.Protect("Endpoint=sb://x/;SharedAccessKey=secret");

        new AesGcmSecretProtector(Key).Unprotect(blob)
            .Should().Be("Endpoint=sb://x/;SharedAccessKey=secret");
    }

    [Fact]
    public void Same_plaintext_encrypts_to_different_blobs()
    {
        var protector = new AesGcmSecretProtector(Key);

        protector.Protect("s").Should().NotEqual(protector.Protect("s")); // random nonce
    }

    [Fact]
    public void Tampered_blob_throws()
    {
        var protector = new AesGcmSecretProtector(Key);
        var blob = protector.Protect("secret");
        blob[^1] ^= 0xFF;

        var act = () => protector.Unprotect(blob);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Key_must_be_32_bytes()
    {
        var act = () => new AesGcmSecretProtector(new byte[16]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unprotect_short_blob_throws_CryptographicException()
    {
        var protector = new AesGcmSecretProtector(Key);
        var shortBlob = new byte[27]; // Less than NonceSize(12) + TagSize(16)

        var act = () => protector.Unprotect(shortBlob);

        act.Should().Throw<CryptographicException>()
            .WithMessage("Ciphertext blob is too short to contain a nonce and tag.");
    }

    [Fact]
    public void Key_is_defensively_copied()
    {
        var keyArray = RandomNumberGenerator.GetBytes(32);
        var keyArrayCopy = (byte[])keyArray.Clone();
        var protector = new AesGcmSecretProtector(keyArray);
        var plaintext = "secret";
        var blob = protector.Protect(plaintext);

        // Mutate the original key array passed to the constructor
        Array.Clear(keyArray);

        // Unprotect should still work because the protector has its own copy
        protector.Unprotect(blob).Should().Be(plaintext);

        // Create a new protector with the mutated key and verify it cannot decrypt
        var protectorWithClearedKey = new AesGcmSecretProtector(keyArrayCopy);
        protectorWithClearedKey.Protect(plaintext).Should().NotEqual(blob);
    }
}
