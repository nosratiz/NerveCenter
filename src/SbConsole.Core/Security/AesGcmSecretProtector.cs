using System.Security.Cryptography;
using System.Text;

namespace SbConsole.Core.Security;

/// <summary>Blob layout: nonce(12) || tag(16) || ciphertext.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Data key must be exactly 32 bytes.", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    public byte[] Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return blob;
    }

    public string Unprotect(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext blob is too short to contain a nonce and tag.");
        }

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
