using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Zipark.Crypto;

/// <summary>
/// AES-256-GCM for cache chunks. The key is random per session, lives only in process memory and is
/// wiped on dispose, so spilled cache files are unreadable after the process exits — decrypted
/// content of encrypted archives never reaches the disk in plaintext.
/// Layout of a sealed chunk: [nonce 12][tag 16][ciphertext n].
/// </summary>
public sealed class ChunkCipher : IDisposable
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize;

    private readonly byte[] _key = new byte[32];
    private readonly AesGcm _aes;
    private long _counter;
    private readonly uint _salt;

    public ChunkCipher()
    {
        RandomNumberGenerator.Fill(_key);
        _aes = new AesGcm(_key, TagSize);
        _salt = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    }

    /// <summary>Encrypts <paramref name="plain"/>; <paramref name="associated"/> binds the chunk to its cache key.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> associated)
    {
        var output = new byte[Overhead + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, _salt);
        BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], Interlocked.Increment(ref _counter));
        _aes.Encrypt(nonce, plain, output.AsSpan(Overhead), output.AsSpan(NonceSize, TagSize), associated);
        return output;
    }

    /// <summary>Decrypts and authenticates; throws <see cref="CryptographicException"/> on tampering.</summary>
    public void Open(ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associated, Span<byte> plain)
    {
        if (sealedData.Length < Overhead || plain.Length != sealedData.Length - Overhead)
            throw new CryptographicException("Invalid sealed chunk length.");
        _aes.Decrypt(sealedData[..NonceSize], sealedData[Overhead..], sealedData.Slice(NonceSize, TagSize), plain, associated);
    }

    public void Dispose()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }
}
