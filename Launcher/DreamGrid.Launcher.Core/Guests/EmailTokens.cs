using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>One stored token: the SHA-256 of the token as lower-case hex, what it is for, whose it is, and when it stops working.</summary>
public sealed record EmailTokenEntry(string Hash, string Purpose, Guid Principal, DateTimeOffset Expires);

/// <summary>
/// Single-use tokens for links sent by email (confirm an address, reset a password). A token is 256 random bits, shown once as
/// base64url; only its SHA-256 hash is kept, with its purpose, the account it belongs to and its expiry, so the file is of no use
/// to anyone who reads it. A token works once, for that purpose and that account, before it expires. Kept in <c>file</c>
/// (<c>&lt;data&gt;/guests/email-tokens.json</c>); a missing or damaged file means no token is valid
/// (<see cref="JsonStateFile{T}"/>). Safe to call from any thread.
/// </summary>
public sealed class EmailTokens
{
    private readonly JsonStateFile<List<EmailTokenEntry>> _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<EmailTokenEntry> _all;

    public EmailTokens(string file, TimeProvider time, ILogger<EmailTokens>? log = null)
    {
        _time = time;
        _file = new JsonStateFile<List<EmailTokenEntry>>(file, () => [], log);
        _all = _file.Load();
    }

    /// <summary>
    /// Makes a token for this purpose and account that works until <paramref name="lifetime"/> has passed. Any older token for the
    /// same purpose and account stops working. The token is returned here and kept nowhere.
    /// </summary>
    public string Issue(string purpose, Guid principal, TimeSpan lifetime)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _all.RemoveAll(e => e.Expires <= now || (e.Principal == principal && e.Purpose == purpose));
            _all.Add(new EmailTokenEntry(HashOf(token), purpose, principal, now + lifetime));
            _file.Save(_all);
        }
        return token;
    }

    /// <summary>
    /// True once, and only for this purpose and this account before the token expires. A token that is recognised but is for
    /// another purpose or account, or has expired, is spent too, so a leaked token can't be tried again against the right one.
    /// Any other string, however odd or long, is simply not a token: false, nothing changes.
    /// </summary>
    public bool TryRedeem(string purpose, Guid principal, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = Encoding.ASCII.GetBytes(HashOf(token));
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var found = false;
            var ok = false;
            foreach (var e in _all)
            {
                if (!CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(e.Hash))) continue;
                found = true;
                ok = e.Purpose == purpose && e.Principal == principal && e.Expires > now;
                _all.Remove(e);
                break;
            }
            if (found)
            {
                _all.RemoveAll(e => e.Expires <= now);
                _file.Save(_all);
            }
            return ok;
        }
    }

    /// <summary>
    /// Whose live token this is, for <paramref name="purpose"/>, without spending it: a page reads it to learn whose link was opened,
    /// then calls <see cref="TryRedeem"/> once the work is done. Null for an expired token, another purpose or anything else.
    /// </summary>
    public Guid? Find(string purpose, string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Encoding.ASCII.GetBytes(HashOf(token));
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var e in _all)
                if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(e.Hash)))
                    return e.Purpose == purpose && e.Expires > now ? e.Principal : null;
            return null;
        }
    }

    /// <summary>
    /// Ends every token for this purpose and account at once, so no link already sent for it works (an email change the owner
    /// cancels by taking their account back).
    /// </summary>
    public void Revoke(string purpose, Guid principal)
    {
        lock (_gate)
        {
            if (_all.RemoveAll(e => e.Principal == principal && e.Purpose == purpose) > 0) _file.Save(_all);
        }
    }

    /// <summary>The token's SHA-256 as lower-case hex, as stored: for another store to tie a record to one link without keeping it.</summary>
    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
