using System.Security.Cryptography;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.OpenSim;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>The avatar name is already on the grid.</summary>
public sealed class NameTakenException(string first, string last) : Exception($"{first} {last} is already on the grid.");

/// <summary>
/// Makes a resident avatar through Robust's console for sign-up (spec 2026-10-31-sign-up-design §5): <c>create user</c> with a
/// new PrincipalID and a random console-safe password (the person's own password is written afterwards through the database, so
/// it never goes on the console line), then <c>set user level</c> when the level isn't 0. Robust runs console commands on its own
/// thread, so each step waits until the account shows it. When anything fails after the account appeared, the account is deleted
/// before the error goes on, so no half-made avatar keeps the name. The caller holds the grid's operation lock.
/// </summary>
public sealed class ResidentAvatars(IRobustClient robust, IAccountRemover remover, TimeProvider time, TimeSpan settle, TimeSpan wait, TimeSpan poll)
{
    /// <summary>
    /// Throws <see cref="NameTakenException"/> when the name is taken (nothing sent), <see cref="HttpRequestException"/> when Robust
    /// can't be reached, and <see cref="InvalidOperationException"/> when Robust didn't make the account or take the level in time.
    /// </summary>
    public async Task<RobustAccount> CreateAsync(RestConsoleClient console, string first, string last, string? email, int level, CancellationToken ct)
    {
        GridConsoleCommands.ValidateAvatarName(first, last);
        // The strict lookup: a reply that isn't an account answer is Robust failing, never "the name is free".
        if (await robust.FindAccountAsync(first, last, ct) is not null) throw new NameTakenException(first, last);

        var id = Guid.NewGuid();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // Once create user is sent the avatar may exist, so from there on it runs to the end even if the request is given up:
        // nothing is left half-made. Only the account with the PrincipalID sent counts; another one with the name (Robust refused
        // create user) is never touched.
        var none = CancellationToken.None;
        RobustAccount? made = null;
        try
        {
            await GridConsoleCommands.CreateUserAsync(console, first, last, password, email, id, settle, none);
            made = await WaitAsync(first, last, a => a.PrincipalId == id, none);
            if (made?.PrincipalId != id)
            {
                made = null;
                throw new InvalidOperationException($"Robust did not create {first} {last} in time.");
            }
            if (level == made.UserLevel) return made;

            await console.RunAsync($"set user level {first} {last} {level}", settle, none);
            var leveled = await WaitAsync(first, last, a => a.PrincipalId == id && a.UserLevel == level, none);
            if (leveled is null || leveled.PrincipalId != id || leveled.UserLevel != level)
                throw new InvalidOperationException($"Robust did not give {first} {last} level {level} in time.");
            return leveled;
        }
        catch when (made is not null)
        {
            try { await remover.DeleteAsync(made.PrincipalId, none); }
            catch (StaffDirectoryUnavailableException) { } // the first failure is the one to report
            throw;
        }
    }

    private async Task<RobustAccount?> WaitAsync(string first, string last, Func<RobustAccount, bool> done, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + wait;
        while (true)
        {
            var account = await robust.GetAccountAsync(first, last, ct);
            if ((account is not null && done(account)) || time.GetUtcNow() >= deadline) return account;
            await Task.Delay(poll, time, ct);
        }
    }
}
