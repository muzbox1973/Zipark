namespace Zipark.Crypto;

public enum StrengthLevel { VeryWeak = 0, Weak = 1, Fair = 2, Strong = 3, VeryStrong = 4 }

public sealed record StrengthReport(StrengthLevel Level, double EntropyBits, IReadOnlyList<string> Hints);

/// <summary>
/// Offline password strength estimate: character-pool entropy with penalties for repetition,
/// sequences and very common passwords. Hint keys are localized by the UI.
/// </summary>
public static class PasswordStrength
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "123456", "12345678", "123456789", "1234567890", "qwerty", "qwerty123", "111111", "000000",
        "abc123", "iloveyou", "admin", "letmein", "welcome", "monkey", "dragon", "1q2w3e4r", "asdfgh", "zxcvbn",
        "password1", "passw0rd", "sunshine", "princess", "football", "baseball", "master", "qwer1234", "q1w2e3r4",
    };

    public static StrengthReport Evaluate(ReadOnlySpan<char> pw)
    {
        var hints = new List<string>();
        if (pw.Length == 0) return new StrengthReport(StrengthLevel.VeryWeak, 0, ["pw.hint.empty"]);

        bool lower = false, upper = false, digit = false, symbol = false, nonAscii = false;
        foreach (var c in pw)
        {
            if (char.IsAsciiLetterLower(c)) lower = true;
            else if (char.IsAsciiLetterUpper(c)) upper = true;
            else if (char.IsAsciiDigit(c)) digit = true;
            else if (c > 127) nonAscii = true;
            else symbol = true;
        }
        int pool = (lower ? 26 : 0) + (upper ? 26 : 0) + (digit ? 10 : 0) + (symbol ? 33 : 0) + (nonAscii ? 100 : 0);
        double bits = pw.Length * Math.Log2(Math.Max(pool, 2));

        // Penalise repeated characters and straight sequences (abc, 123, cba).
        int repeats = 0, sequences = 0;
        for (int i = 1; i < pw.Length; i++)
        {
            if (pw[i] == pw[i - 1]) repeats++;
            if (Math.Abs(pw[i] - pw[i - 1]) == 1) sequences++;
        }
        bits -= repeats * 2.5 + sequences * 2.0;

        if (Common.Contains(pw.ToString()))
        {
            bits = Math.Min(bits, 10);
            hints.Add("pw.hint.common");
        }
        if (pw.Length < 8) hints.Add("pw.hint.short");
        if (pw.Length < 12) hints.Add("pw.hint.longer");
        if (!(lower && upper)) hints.Add("pw.hint.mixcase");
        if (!digit) hints.Add("pw.hint.digit");
        if (!symbol) hints.Add("pw.hint.symbol");
        if (repeats + sequences > pw.Length / 3) hints.Add("pw.hint.pattern");

        bits = Math.Max(0, bits);
        var level = bits switch
        {
            < 28 => StrengthLevel.VeryWeak,
            < 40 => StrengthLevel.Weak,
            < 60 => StrengthLevel.Fair,
            < 80 => StrengthLevel.Strong,
            _ => StrengthLevel.VeryStrong,
        };
        if (pw.Length < 8 && level > StrengthLevel.Weak) level = StrengthLevel.Weak;
        return new StrengthReport(level, bits, hints);
    }
}

public enum ZipEncryption
{
    /// <summary>WinZip AES-256 (recommended).</summary>
    Aes256 = 0,
    /// <summary>Legacy ZipCrypto — weak, only offered with an explicit warning for old tools.</summary>
    ZipCrypto = 1,
}

public static class EncryptionAdvisor
{
    /// <summary>Returns localized-warning keys for the chosen settings.</summary>
    public static IReadOnlyList<string> Review(Zipark.Core.ArchiveFormat format, ZipEncryption zipMethod, bool encryptNames, StrengthLevel strength)
    {
        var w = new List<string>();
        if (format == Zipark.Core.ArchiveFormat.Zip && zipMethod == ZipEncryption.ZipCrypto) w.Add("enc.warn.zipcrypto");
        if (format == Zipark.Core.ArchiveFormat.Zip) w.Add("enc.warn.zipnames");
        if (format == Zipark.Core.ArchiveFormat.SevenZip && !encryptNames) w.Add("enc.warn.7znames");
        if (strength <= StrengthLevel.Weak) w.Add("enc.warn.weakpw");
        if (!Zipark.Core.ArchiveFormats.SupportsEncryption(format)) w.Add("enc.warn.unsupported");
        return w;
    }
}
