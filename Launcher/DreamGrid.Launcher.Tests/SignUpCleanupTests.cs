using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Removing sign-ups nobody confirmed within 24 hours (spec 2026-10-31-sign-up-design §5 step 6).</summary>
public sealed class SignUpCleanupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SignUpCleanup Cleanup(SignUpsTests.Rig rig) => rig.F.Services.GetRequiredService<SignUpCleanup>();

    private static async Task<(SignUpsTests.Rig Rig, Guid Bea)> PendingAsync()
    {
        var rig = SignUpsTests.Factory();
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), null, Ct);
        return (rig, rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId);
    }

    [Fact]
    public async Task Deletes_listed_older_than_24h()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.F.Time.Advance(TimeSpan.FromHours(25));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.DoesNotContain(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.False(rig.Pending.Contains(bea));
        Assert.Equal([bea], rig.F.Remover.Deleted);
    }

    [Fact]
    public async Task Keeps_listed_younger()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.F.Time.Advance(TimeSpan.FromHours(23));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.True(rig.Pending.Contains(bea));
    }

    [Fact]
    public async Task A_link_sent_again_counts_from_the_new_link()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.F.Time.Advance(TimeSpan.FromHours(23));
        await rig.SignUps.ResendAsync("Bea", "Example", null, Ct);
        rig.F.Time.Advance(TimeSpan.FromHours(2));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.True(rig.Pending.Contains(bea));
    }

    [Fact]
    public async Task Never_touches_unlisted_minus_one()
    {
        await using var rig = SignUpsTests.Factory();
        var sue = Guid.NewGuid();
        rig.F.Robust.Add("Sue", "Spended", -1, "old-password-1", sue);
        rig.F.Time.Advance(TimeSpan.FromDays(30));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == sue);
        Assert.Empty(rig.F.Remover.Deleted);
    }

    [Fact]
    public async Task Confirmed_then_suspended_is_kept()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        Assert.NotNull(await rig.SignUps.ConfirmAsync(token, null, Ct));
        rig.F.Robust.SetLevel("Bea", "Example", -1);
        rig.F.Time.Advance(TimeSpan.FromDays(3));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.Empty(rig.F.Remover.Deleted);
    }

    [Fact]
    public async Task Listed_but_already_confirmed_elsewhere_is_only_unlisted()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.F.Robust.SetLevel("Bea", "Example", 0);
        rig.F.Time.Advance(TimeSpan.FromHours(25));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.False(rig.Pending.Contains(bea));
        Assert.Empty(rig.F.Remover.Deleted);
    }

    [Fact]
    public async Task Database_down_keeps_the_list()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        // Down before the clock moves: moving it also wakes the host's own clean-up.
        rig.F.Remover.Down = true;
        rig.F.Time.Advance(TimeSpan.FromHours(25));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.True(rig.Pending.Contains(bea));
        rig.F.Remover.Down = false;
        await Cleanup(rig).RunOnceAsync(Ct);
        Assert.False(rig.Pending.Contains(bea));
    }
}
