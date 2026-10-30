using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// Time-based one-time passwords as RFC 6238 defines them: HMAC-SHA1, 6 digits, 30-second steps. Authenticator apps
/// (Google Authenticator, Authy, Microsoft Authenticator, a password manager) all speak this.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new random secret: 20 bytes, the length of the HMAC-SHA1 output.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>The secret as RFC 4648 Base32 without padding, which is what an authenticator app asks for.</summary>
    public static string Base32(byte[] secret)
    {
        var sb = new StringBuilder((secret.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in secret)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>The code for one 30-second step.</summary>
    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The step a moment falls in: Unix seconds divided by 30.</summary>
    public static long StepAt(DateTimeOffset t) => Math.DivRem(t.ToUnixTimeSeconds(), PeriodSeconds, out var rem) - (rem < 0 ? 1 : 0);

    /// <summary>
    /// Whether <paramref name="code"/> (spaces ignored) is the code of the current step or the one before or after (clocks drift),
    /// and that step is newer than <paramref name="lastUsedStep"/>: a code can be used once, even inside its window.
    /// <paramref name="matchedStep"/> is the step that matched. The three candidates are always all compared, in constant time.
    /// </summary>
    public static bool Verify(byte[] secret, string code, DateTimeOffset now, long? lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        if (code is null) return false;
        var typed = new string([.. code.Where(c => c != ' ')]);
        if (typed.Length != Digits || !typed.All(char.IsAsciiDigit)) return false;

        var typedBytes = Encoding.ASCII.GetBytes(typed);
        var current = StepAt(now);
        var found = false;
        for (var step = current - 1; step <= current + 1; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(typedBytes, Encoding.ASCII.GetBytes(Code(secret, step)));
            if (equal && (lastUsedStep is not { } last || step > last))
            {
                found = true;
                matchedStep = step;
            }
        }
        return found;
    }

    /// <summary>The <c>otpauth://</c> address an authenticator app reads from a QR code or a link.</summary>
    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={PeriodSeconds}";
}
