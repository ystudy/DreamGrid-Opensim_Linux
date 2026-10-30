using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Who has two-factor sign-in, their authenticator secret and their backup codes. Kept in <c>file</c>
/// (<c>&lt;data&gt;/security/twofactor.json</c>, written atomically). A secret is stored only protected by ASP.NET data
/// protection (purpose <c>DreamGrid.TwoFactor.v1</c>); backup codes only as SHA-256 hashes. The file is read on every call,
/// never cached, so a change made by another process (the command-line verb that turns two-factor off for someone) is seen at
/// once. A missing file means nobody has two-factor; a damaged one means <see cref="TwoFactorState.Unknown"/> and every
/// check fails: it never reads as "off". Nothing writes over a damaged file (writes throw <see cref="InvalidOperationException"/>)
/// until <see cref="MoveAsideIfDamaged"/> has set it aside. Secrets, codes and the file's content are never logged.
/// Safe to call from any thread of this process.
/// </summary>
public sealed class TwoFactorStore(string file, IDataProtectionProvider dp, TimeProvider time, ILogger<TwoFactorStore>? log = null) : ITwoFactorStatus
{
    private const string Purpose = "DreamGrid.TwoFactor.v1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector _protector = dp.CreateProtector(Purpose);
    private readonly Lock _gate = new();
    private bool _unreadableLogged;

    /// <summary>The saved form.</summary>
    private sealed record State(Dictionary<Guid, Entry?>? People);

    /// <summary>
    /// One person. <c>Secret</c> is the confirmed authenticator secret (protected); without it two-factor is off for them.
    /// <c>Pending</c> is a secret shown but not yet confirmed, kept apart so starting again never switches a working one off.
    /// <c>LastStep</c> is the newest step already used.
    /// </summary>
    private sealed record Entry(string? Name, string? Secret, string? Pending, string? PendingName, long? LastStep, List<string>? Backup);

    public TwoFactorState StateOf(Guid principal)
    {
        lock (_gate)
        {
            if (!TryRead(out var state)) return TwoFactorState.Unknown;
            return state.TryGetValue(principal, out var e) && e.Secret is { Length: > 0 } ? TwoFactorState.On : TwoFactorState.Off;
        }
    }

    /// <summary>
    /// Starts (or restarts) enrolment: a new secret, kept protected as pending until <see cref="ConfirmEnrol"/>, and the address
    /// for the authenticator app. A secret already confirmed stays in force meanwhile. Throws if the file is damaged.
    /// </summary>
    public (byte[] Secret, string Uri) BeginEnrol(Guid principal, string accountName, string issuer)
    {
        lock (_gate)
        {
            var state = ReadForWrite();
            var secret = Totp.NewSecret();
            var old = state.GetValueOrDefault(principal);
            state[principal] = (old ?? new Entry(null, null, null, null, null, null)) with
            {
                Pending = Protect(secret),
                PendingName = accountName,
            };
            Save(state);
            return (secret, Totp.OtpAuthUri(issuer, accountName, secret));
        }
    }

    /// <summary>
    /// Whether <paramref name="shown"/> is the secret waiting to be confirmed for the person: false once setup was started again
    /// elsewhere (another browser), confirmed, or when the file is damaged.
    /// </summary>
    public bool IsPending(Guid principal, byte[] shown)
    {
        lock (_gate)
        {
            if (!TryRead(out var state)) return false;
            return state.TryGetValue(principal, out var e) && e.Pending is { Length: > 0 } pending
                && Unprotect(pending) is { } secret && CryptographicOperations.FixedTimeEquals(secret, shown);
        }
    }

    /// <summary>
    /// Switches two-factor on if <paramref name="code"/> is right for the pending secret, and returns the ten backup codes (shown
    /// once). Returns null, changing nothing, if there is no pending secret, the code is wrong, or <paramref name="shown"/> is
    /// given and is not the pending secret (setup was started again in another browser, whose secret this one never showed).
    /// </summary>
    public IReadOnlyList<string>? ConfirmEnrol(Guid principal, string code, byte[]? shown = null)
    {
        lock (_gate)
        {
            var state = ReadForWrite();
            if (!state.TryGetValue(principal, out var e) || e.Pending is not { Length: > 0 } pending) return null;
            var secret = Unprotect(pending);
            if (secret is null || shown is not null && !CryptographicOperations.FixedTimeEquals(secret, shown)) return null;
            if (!Totp.Verify(secret, code, time.GetUtcNow(), null, out var step)) return null;

            var backup = BackupCodes.New();
            state[principal] = new Entry(e.PendingName ?? e.Name, pending, null, null, step, [.. backup.Select(BackupCodes.Hash)]);
            Save(state);
            return backup;
        }
    }

    /// <summary>
    /// Whether <paramref name="codeOrBackup"/> signs the person in: six digits are tried as an authenticator code (its step is then
    /// used up), anything else as a backup code (which is then used up). False for anyone without two-factor and whenever the
    /// file is damaged. Throws nothing for a failed save: a code that cannot be marked used is refused.
    /// </summary>
    public bool VerifyCode(Guid principal, string codeOrBackup, out bool usedBackup)
    {
        usedBackup = false;
        lock (_gate)
        {
            if (!TryRead(out var state)) return false;
            if (!state.TryGetValue(principal, out var e) || e.Secret is not { Length: > 0 } protectedSecret) return false;

            var typed = new string([.. (codeOrBackup ?? "").Where(c => c != ' ')]);
            try
            {
                if (typed.Length == Totp.Digits && typed.All(char.IsAsciiDigit))
                {
                    var secret = Unprotect(protectedSecret);
                    if (secret is null || !Totp.Verify(secret, typed, time.GetUtcNow(), e.LastStep, out var step)) return false;
                    state[principal] = e with { LastStep = step };
                    Save(state);
                    return true;
                }

                if (typed.Length == 0) return false;
                var hash = Encoding.ASCII.GetBytes(BackupCodes.Hash(typed));
                var backup = e.Backup ?? [];
                var at = -1;
                for (var i = 0; i < backup.Count; i++) // every hash is compared, so the time does not say which matched
                    if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(backup[i] ?? ""))) at = i;
                if (at < 0) return false;
                var rest = new List<string>(backup);
                rest.RemoveAt(at);
                state[principal] = e with { Backup = rest };
                Save(state);
                usedBackup = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Replaces all the person's backup codes with ten new ones (shown once) if <paramref name="code"/> is a current authenticator
    /// code (a backup code is not accepted; its step is then used up). Returns null, changing nothing, for anyone without
    /// two-factor or a wrong code. Throws if the file is damaged (<see cref="InvalidOperationException"/>) or cannot be saved.
    /// </summary>
    public IReadOnlyList<string>? RegenerateBackupCodes(Guid principal, string code)
    {
        lock (_gate)
        {
            var state = ReadForWrite();
            if (!state.TryGetValue(principal, out var e) || e.Secret is not { Length: > 0 } protectedSecret) return null;
            var secret = Unprotect(protectedSecret);
            if (secret is null || !Totp.Verify(secret, code, time.GetUtcNow(), e.LastStep, out var step)) return null;

            var backup = BackupCodes.New();
            state[principal] = e with { LastStep = step, Backup = [.. backup.Select(BackupCodes.Hash)] };
            Save(state);
            return backup;
        }
    }

    /// <summary>Turns two-factor off for the person and forgets their secret and codes. Throws if the file is damaged.</summary>
    public void Disable(Guid principal)
    {
        lock (_gate)
        {
            var state = ReadForWrite();
            if (state.Remove(principal)) Save(state);
        }
    }

    /// <summary>Everyone with two-factor on, by the account name given at enrolment. Empty when the file is damaged.</summary>
    public IReadOnlyList<(Guid Principal, string Name)> Enrolled()
    {
        lock (_gate)
        {
            if (!TryRead(out var state)) return [];
            return [.. state.Where(kv => kv.Value.Secret is { Length: > 0 }).Select(kv => (kv.Key, kv.Value.Name ?? ""))];
        }
    }

    /// <summary>
    /// If <paramref name="file"/> exists but cannot be read, renames it to <c>twofactor.json.damaged-&lt;utc yyyyMMddHHmmss&gt;</c> so
    /// the store starts empty (the people affected enrol again). A missing or readable file is left alone.
    /// </summary>
    public static void MoveAsideIfDamaged(string file)
    {
        if (!File.Exists(file) || TryParse(file, out _, out var damaged) || !damaged) return;
        var aside = file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.Move(file, aside, overwrite: true);
    }

    private string Protect(byte[] secret) => Convert.ToBase64String(_protector.Protect(secret));

    /// <summary>The secret, or null if it cannot be unprotected (its key is gone): then nothing verifies, which is the safe answer.</summary>
    private byte[]? Unprotect(string saved)
    {
        try
        {
            return _protector.Unprotect(Convert.FromBase64String(saved));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private Dictionary<Guid, Entry> ReadForWrite() =>
        TryRead(out var state) ? state : throw new InvalidOperationException("The two-factor file is damaged and has not been changed.");

    /// <summary>The people in the file (none if it is missing); false when it cannot be read or is the wrong shape.</summary>
    private bool TryRead(out Dictionary<Guid, Entry> people)
    {
        if (TryParse(file, out people, out _))
        {
            _unreadableLogged = false;
            return true;
        }
        if (!_unreadableLogged) // once, until it reads again
            log?.LogWarning("The two-factor file {File} could not be read; two-factor sign-in is refused until it is repaired or set aside.", file);
        _unreadableLogged = true;
        return false;
    }

    /// <param name="damaged">True when the file exists and its content is no good (not when it is merely locked at the moment).</param>
    private static bool TryParse(string path, out Dictionary<Guid, Entry> people, out bool damaged)
    {
        people = [];
        damaged = false;
        string json;
        try
        {
            if (!File.Exists(path)) return true;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            json = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            var state = JsonSerializer.Deserialize<State>(json, Options) ?? throw new JsonException("the file contains null");
            var read = new Dictionary<Guid, Entry>();
            foreach (var (principal, entry) in state.People ?? [])
            {
                // An entry that is switched on but has no secret to check can only mean damage.
                if (entry is null || entry.Secret is { Length: 0 }) throw new JsonException("an empty entry");
                read[principal] = entry;
            }
            people = read;
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            damaged = true;
            return false;
        }
    }

    private void Save(Dictionary<Guid, Entry> people) =>
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(new State(people.ToDictionary(kv => kv.Key, kv => (Entry?)kv.Value)), Options));
}
