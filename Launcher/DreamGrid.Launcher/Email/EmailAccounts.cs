using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Email;
using Microsoft.AspNetCore.DataProtection;

namespace DreamGrid.Launcher.Email;

/// <summary>
/// The mail accounts the grid sends through, in the owner's order (1 is tried first). Kept in <c>file</c>
/// (<c>&lt;data&gt;/email/accounts.json</c>, <see cref="JsonStateFile{T}"/>); a missing or damaged file means no accounts. A
/// password is stored only protected by data protection (purpose <c>DreamGrid.Email.Password.v1</c>) and is never logged; one
/// that can't be unprotected (the key ring was replaced) reads as no password, so the account isn't used. Safe to call from any
/// thread of this process.
/// </summary>
public sealed class EmailAccounts
{
    private const string Purpose = "DreamGrid.Email.Password.v1";

    private readonly JsonStateFile<List<EmailAccount>> _file;
    private readonly IDataProtector _protector;
    private readonly Lock _gate = new();
    private readonly List<EmailAccount> _all;

    public EmailAccounts(string file, IDataProtectionProvider dp, ILogger<EmailAccounts>? log = null)
    {
        _file = new JsonStateFile<List<EmailAccount>>(file, () => [], log);
        _protector = dp.CreateProtector(Purpose);
        _all = [.. _file.Load().OrderBy(a => a.Order)];
        Renumber();
    }

    public IReadOnlyList<EmailAccount> All()
    {
        lock (_gate) return [.. _all];
    }

    public EmailAccount? Find(Guid id)
    {
        lock (_gate) return _all.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>
    /// Adds the account at the end, or replaces the one with its id (keeping its place). A blank <paramref name="newPassword"/>
    /// keeps the stored password. Saving clears any problem: the owner has looked at it. False if the file couldn't be written.
    /// </summary>
    public bool Save(EmailAccount account, string? newPassword)
    {
        lock (_gate)
        {
            var i = _all.FindIndex(a => a.Id == account.Id);
            var password = string.IsNullOrEmpty(newPassword)
                ? (i >= 0 ? _all[i].ProtectedPassword : null)
                : _protector.Protect(newPassword);
            var saved = account with { ProtectedPassword = password, Problem = null, ProblemSince = null };
            if (i >= 0) _all[i] = saved with { Order = _all[i].Order, Paused = account.Paused };
            else _all.Add(saved with { Order = _all.Count + 1 });
            return Commit();
        }
    }

    /// <summary>Deletes the account and its stored password. False if there was none or the file couldn't be written.</summary>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (_all.RemoveAll(a => a.Id == id) == 0) return false;
            Renumber();
            return Commit();
        }
    }

    /// <summary>Moves the account <paramref name="delta"/> places (−1 is up). False when it can't move that way.</summary>
    public bool Move(Guid id, int delta)
    {
        lock (_gate)
        {
            var i = _all.FindIndex(a => a.Id == id);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= _all.Count || delta == 0) return false;
            var a = _all[i];
            _all.RemoveAt(i);
            _all.Insert(j, a);
            Renumber();
            return Commit();
        }
    }

    public bool SetPaused(Guid id, bool paused) => Change(id, a => a with { Paused = paused });

    /// <summary>Marks the account as not working (<paramref name="problem"/>, plain words) or, with null, working again.</summary>
    public bool MarkProblem(Guid id, string? problem, DateTimeOffset now) =>
        Change(id, a => a with { Problem = problem, ProblemSince = problem is null ? null : now });

    /// <summary>The account's password, or null when it has none or it can't be unprotected.</summary>
    public string? PasswordOf(EmailAccount a)
    {
        if (string.IsNullOrEmpty(a.ProtectedPassword)) return null;
        try { return _protector.Unprotect(a.ProtectedPassword); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>Whether some account can send: not paused, no problem, and a password that can be read.</summary>
    public bool IsReady
    {
        get
        {
            lock (_gate) return _all.Any(a => !a.Paused && a.Problem is null && PasswordOf(a) is not null);
        }
    }

    private bool Change(Guid id, Func<EmailAccount, EmailAccount> change)
    {
        lock (_gate)
        {
            var i = _all.FindIndex(a => a.Id == id);
            if (i < 0) return false;
            _all[i] = change(_all[i]);
            return Commit();
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < _all.Count; i++) _all[i] = _all[i] with { Order = i + 1 };
    }

    private bool Commit() => _file.Save([.. _all]);
}
