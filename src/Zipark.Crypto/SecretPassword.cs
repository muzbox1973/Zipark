using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Zipark.Crypto;

/// <summary>
/// Password held in a pinned char buffer that is wiped on dispose. It is never serialized,
/// logged or written to disk; it only travels to the engine through a redirected stdin pipe.
/// </summary>
public sealed class SecretPassword : IDisposable
{
    private char[]? _chars;
    private GCHandle _pin;

    private SecretPassword(char[] chars)
    {
        _chars = chars;
        _pin = GCHandle.Alloc(_chars, GCHandleType.Pinned);
    }

    public static SecretPassword FromChars(ReadOnlySpan<char> chars)
    {
        var copy = GC.AllocateUninitializedArray<char>(chars.Length, pinned: false);
        chars.CopyTo(copy);
        return new SecretPassword(copy);
    }

    public static SecretPassword FromSecureString(SecureString secure)
    {
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            unsafe
            {
                var span = new ReadOnlySpan<char>((void*)ptr, secure.Length);
                return FromChars(span);
            }
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    public int Length => _chars?.Length ?? 0;
    public bool IsEmpty => Length == 0;

    public ReadOnlySpan<char> Span => _chars ?? throw new ObjectDisposedException(nameof(SecretPassword));

    public SecretPassword Clone() => FromChars(Span);

    /// <summary>Writes the password plus a newline to a stream (e.g. the engine's stdin) without creating a string.</summary>
    public void WriteLineTo(Stream stream)
    {
        var enc = new UTF8Encoding(false);
        var bytes = new byte[enc.GetMaxByteCount(Length + 2)];
        try
        {
            int n = enc.GetBytes(Span, bytes);
            bytes[n++] = (byte)'\r';
            bytes[n++] = (byte)'\n';
            stream.Write(bytes, 0, n);
            stream.Flush();
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public bool ContentEquals(SecretPassword? other) =>
        other is not null && Span.SequenceEqual(other.Span);

    public void Dispose()
    {
        if (_chars is null) return;
        Array.Clear(_chars);
        if (_pin.IsAllocated) _pin.Free();
        _chars = null;
    }

    public override string ToString() => "********";
}
