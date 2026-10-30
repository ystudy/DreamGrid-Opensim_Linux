using System.Security.Cryptography;
using System.Text;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// One-time codes for when the authenticator app is not to hand. Only the SHA-256 of a code is ever stored; the codes themselves
/// are shown once, at enrolment. The alphabet leaves out the letters easily mistaken for digits (i, l, o).
/// </summary>
public static class BackupCodes
{
    public const int Count = 10;

    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>Ten distinct codes shaped <c>xxxxx-xxxxx</c>.</summary>
    public static IReadOnlyList<string> New()
    {
        var codes = new List<string>(Count);
        while (codes.Count < Count)
        {
            var code = Part() + "-" + Part();
            if (!codes.Contains(code)) codes.Add(code);
        }
        return codes;
    }

    /// <summary>The stored form: lower-case, without dash or spaces, SHA-256 as lower-case hex.</summary>
    public static string Hash(string code)
    {
        var clean = new string([.. code.Where(c => c is not ('-' or ' ')).Select(char.ToLowerInvariant)]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clean)));
    }

    private static string Part() => string.Create(5, 0, static (span, _) =>
    {
        for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
    });
}
