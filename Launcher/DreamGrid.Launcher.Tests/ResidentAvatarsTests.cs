using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Making a resident avatar through Robust's console for sign-up (spec 2026-10-31-sign-up-design §5). All names are made up.</summary>
public sealed partial class ResidentAvatarsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("^create user (\\S+) (\\S+) (\\S+) \"([^\"]*)\" (\\S+) \"\"$")]
    private static partial Regex CreateLine();

    [GeneratedRegex("^set user level (\\S+) (\\S+) (-?\\d+)$")]
    private static partial Regex LevelLine();

    private readonly FakeRobustAccounts _robust = new();
    private readonly FakeRemover _remover = new();

    /// <summary>When set, Robust "takes" <c>create user</c> but the level command leaves the account as it was.</summary>
    private bool _ignoreLevel;

    private FakeRestConsole Console() => new(cmd =>
    {
        if (CreateLine().Match(cmd) is { Success: true } c)
        {
            _robust.Add(c.Groups[1].Value, c.Groups[2].Value, 0, c.Groups[3].Value, Guid.Parse(c.Groups[5].Value), c.Groups[4].Value);
            return [("Account created", false)];
        }
        if (LevelLine().Match(cmd) is { Success: true } l && !_ignoreLevel)
            _robust.SetLevel(l.Groups[1].Value, l.Groups[2].Value, int.Parse(l.Groups[3].Value));
        return [];
    });

    private ResidentAvatars Avatars() => new(_robust, _remover, TimeProvider.System,
        settle: TimeSpan.FromMilliseconds(50), wait: TimeSpan.FromMilliseconds(500), poll: TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Creates_at_level_minus_one()
    {
        var console = Console();

        var account = await Avatars().CreateAsync(console.Client, "Ann", "Example", "ann@example.test", -1, Ct);

        Assert.Equal(-1, account.UserLevel);
        Assert.Equal("ann@example.test", account.Email);
        Assert.StartsWith("create user Ann Example ", console.Commands[0]);
        Assert.Equal("set user level Ann Example -1", console.Commands[1]);
    }

    [Fact]
    public async Task Level_zero_skips_set_level()
    {
        var console = Console();

        var account = await Avatars().CreateAsync(console.Client, "Ann", "Example", null, 0, Ct);

        Assert.Equal(0, account.UserLevel);
        Assert.DoesNotContain(console.Commands, c => c.StartsWith("set user level"));
    }

    [Fact]
    public async Task Second_create_with_same_name_is_taken()
    {
        var console = Console();
        await Avatars().CreateAsync(console.Client, "Ann", "Example", null, 0, Ct);

        await Assert.ThrowsAsync<NameTakenException>(() => Avatars().CreateAsync(console.Client, "Ann", "Example", null, 0, Ct));
        Assert.Single(console.Commands, c => c.StartsWith("create user"));
        Assert.Empty(_remover.Deleted);
    }

    [Fact]
    public async Task Console_password_is_32_hex()
    {
        var console = Console();

        await Avatars().CreateAsync(console.Client, "Ann", "Example", null, 0, Ct);

        Assert.Matches("^[0-9A-F]{32}$", CreateLine().Match(console.Commands[0]).Groups[3].Value);
    }

    [Fact]
    public async Task Level_not_taken_deletes_the_avatar()
    {
        _ignoreLevel = true;
        var console = Console();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Avatars().CreateAsync(console.Client, "Ann", "Example", null, -1, Ct));

        var made = _robust.All.Single();
        Assert.Equal([made.PrincipalId], _remover.Deleted);
    }

    [Fact]
    public async Task Robust_down_makes_nothing()
    {
        _robust.Down = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => Avatars().CreateAsync(Console().Client, "Ann", "Example", null, 0, Ct));
        Assert.Empty(_remover.Deleted);
    }

    [Fact]
    public async Task Existing_name_with_a_bad_reply_is_never_touched()
    {
        var ann = Guid.NewGuid();
        _robust.Add("Ann", "Example", 0, "keep-me-please", ann);
        _robust.FailureReply = true;
        var console = Console();

        await Assert.ThrowsAnyAsync<Exception>(() => Avatars().CreateAsync(console.Client, "Ann", "Example", null, -1, Ct));

        Assert.Empty(console.Commands);
        _robust.FailureReply = false;
        var still = _robust.All.Single();
        Assert.Equal((ann, 0), (still.PrincipalId, still.UserLevel));
        Assert.True(await _robust.AuthenticateAsync(ann, "keep-me-please", Ct));
        Assert.Empty(_remover.Deleted);
    }

    [Fact]
    public async Task Only_the_account_it_made_counts()
    {
        // Robust refuses create user (the name appeared meanwhile): the account with that name isn't the one asked for.
        var other = Guid.NewGuid();
        var console = new FakeRestConsole(cmd =>
        {
            if (cmd.StartsWith("create user")) _robust.Add("Ann", "Example", 0, "someone-else", other);
            return [];
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Avatars().CreateAsync(console.Client, "Ann", "Example", null, -1, Ct));

        Assert.DoesNotContain(console.Commands, c => c.StartsWith("set user level"));
        Assert.Empty(_remover.Deleted);
    }

    [Fact]
    public async Task Cancelled_after_create_still_cleans_up()
    {
        using var cts = new CancellationTokenSource();
        _ignoreLevel = true;
        var console = new FakeRestConsole(cmd =>
        {
            if (CreateLine().Match(cmd) is { Success: true } c)
            {
                _robust.Add(c.Groups[1].Value, c.Groups[2].Value, 0, c.Groups[3].Value, Guid.Parse(c.Groups[5].Value));
                cts.Cancel();
            }
            return [];
        });

        await Assert.ThrowsAnyAsync<Exception>(() => Avatars().CreateAsync(console.Client, "Ann", "Example", null, -1, cts.Token));

        Assert.Equal([_robust.All.Single().PrincipalId], _remover.Deleted);
    }

    [Fact]
    public void Remover_sql_names_every_table()
    {
        var sql = string.Join("\n", AccountRemover.Statements);
        foreach (var table in new[] { "inventoryitems", "inventoryfolders", "Avatars", "GridUser", "Presence", "tokens", "auth", "UserAccounts" })
            Assert.Contains($"`robust`.`{table}`", sql);
        Assert.All(AccountRemover.Statements, s => Assert.Contains("@id", s));
    }

    private sealed class FakeRemover : IAccountRemover
    {
        public List<Guid> Deleted { get; } = [];

        public Task DeleteAsync(Guid principal, CancellationToken ct)
        {
            Deleted.Add(principal);
            return Task.CompletedTask;
        }
    }
}
