using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Removing sign-ups nobody let in (spec 2026-10-31-sign-up-design §5 step 6, 2026-11-01-sign-up-approval-design §9).</summary>
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
    public async Task Deletes_waiting_after_30_days()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.F.Time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromHours(1));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.DoesNotContain(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.False(rig.Pending.Contains(bea));
        Assert.Equal([bea], rig.F.Remover.Deleted);
    }

    [Fact]
    public async Task Waits_for_an_approval_in_progress_and_keeps_the_account()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        var gate = rig.F.Services.GetRequiredService<SignUpLock>().Gate;

        // An approval holds the lock: the clean-up must wait for it, then see the account is no longer waiting. The lock is
        // taken before the clock moves, since moving it also wakes the host's own clean-up.
        await gate.WaitAsync(Ct);
        rig.F.Time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromHours(1));
        var pass = Cleanup(rig).RunOnceAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        rig.F.Robust.SetLevel("Bea", "Example", 0);
        rig.Pending.TryFinish(bea, SignUpState.Approved, "Test Owner");
        gate.Release();
        await pass;

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.Equal(SignUpState.Approved, rig.Pending.Get(bea)!.State);
        Assert.Empty(rig.F.Remover.Deleted);
    }

    [Fact]
    public async Task Keeps_waiting_at_29_days()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.F.Time.Advance(TimeSpan.FromDays(29));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.Equal(SignUpState.WaitingForEmail, rig.Pending.Get(bea)!.State);
    }

    [Fact]
    public async Task Deletes_unlisted_after_24_hours()
    {
        await using var rig = SignUpsTests.Factory();
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), null, Ct);
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId;
        rig.F.Time.Advance(TimeSpan.FromHours(23));
        await Cleanup(rig).RunOnceAsync(Ct);
        Assert.True(rig.Pending.Contains(bea));

        rig.F.Time.Advance(TimeSpan.FromHours(2));
        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.DoesNotContain(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.Null(rig.Pending.Get(bea));
    }

    [Fact]
    public async Task Drops_entry_raised_elsewhere_without_deleting()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.F.Robust.SetLevel("Bea", "Example", 0);

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Contains(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.Null(rig.Pending.Get(bea));
        Assert.Empty(rig.F.Remover.Deleted);
    }

    [Fact]
    public async Task Fills_missing_name()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        rig.Pending.SetName(bea, "");

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Equal("Bea Example", rig.Pending.Get(bea)!.Name);
    }

    [Fact]
    public async Task Prunes_finished_after_14_days_account_untouched()
    {
        var (rig, bea) = await PendingAsync();
        await using var _ = rig;
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        await rig.SignUps.ConfirmAsync(token, null, Ct);
        rig.F.Time.Advance(TimeSpan.FromDays(13));
        await Cleanup(rig).RunOnceAsync(Ct);
        Assert.Equal(SignUpState.SelfApproved, rig.Pending.Get(bea)!.State);

        rig.F.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.Null(rig.Pending.Get(bea));
        Assert.Equal(0, rig.F.Robust.All.Single(a => a.PrincipalId == bea).UserLevel);
        Assert.Empty(rig.F.Remover.Deleted);
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
        Assert.NotNull((await rig.SignUps.ConfirmAsync(token, null, Ct)).Account);
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
        rig.F.Time.Advance(TimeSpan.FromDays(31));

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
        rig.F.Time.Advance(TimeSpan.FromDays(31));

        await Cleanup(rig).RunOnceAsync(Ct);

        Assert.True(rig.Pending.Contains(bea));
        rig.F.Remover.Down = false;
        await Cleanup(rig).RunOnceAsync(Ct);
        Assert.False(rig.Pending.Contains(bea));
    }
}
