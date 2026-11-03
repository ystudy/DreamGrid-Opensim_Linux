using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Reset links staff send from the People page: no code asked, two-factor cleared once used (spec 2026-11-03-people-admin-design).</summary>
public sealed partial class PasswordResetTests
{
    private static readonly RecoverySettings OffForResidents = new(RecoveryRule.Off, RecoveryRule.EmailLink, RecoveryRule.EmailLink);

    private static PasswordResets Resets(Rig rig) => rig.F.Services.GetRequiredService<PasswordResets>();

    private static TwoFactorStore ResidentStore(Rig rig) => rig.F.Services.GetRequiredService<ResidentTwoFactor>().Store;

    private static async Task<Core.OpenSim.RobustAccount> AnnAsync(Rig rig) => (await rig.F.Robust.FindAccountByIdAsync(AnnId, Ct))!;

    /// <summary>Has staff send Ann a link and returns the token from the email it gets.</summary>
    private static async Task<string> StaffLinkAsync(Rig rig)
    {
        List<string> Links()
        {
            lock (rig.Transport.Sent)
                return [.. rig.Transport.Sent.Where(m => m.To == "ann@example.com" && m.Email.Subject == "Choose a new password for Avalon")
                    .Select(m => System.Text.RegularExpressions.Regex.Match(m.Email.Text, @"/guest/reset-password\?token=([A-Za-z0-9_-]+)"))
                    .Where(m => m.Success).Select(m => m.Groups[1].Value)];
        }
        var before = Links().Count;
        Assert.Equal(StaffResetResult.Sent, Resets(rig).SendByStaff(await AnnAsync(rig)));
        for (var i = 0; i < 200 && Links().Count <= before; i++) await Task.Delay(25, Ct);
        var links = Links();
        Assert.True(links.Count > before, "no staff reset email was sent");
        return links[^1];
    }

    [Fact]
    public async Task Staff_link_needs_no_code_and_clears_two_factor()
    {
        await using var rig = Factory(CodeForAll);
        Enrol(rig, ResidentStore(rig), AnnId, "Ann Smith");

        var token = await StaffLinkAsync(rig);
        Assert.Null((await PageAsync(Client(rig), "/guest/reset-password?token=" + token)).QuerySelector("input[name=Code]"));
        Assert.NotNull(Done(await ResetAsync(Client(rig), token)));

        Assert.True(await PasswordWorksAsync(rig, AnnId, NewPassword));
        Assert.Equal(TwoFactorState.Off, ResidentStore(rig).StateOf(AnnId));
        Assert.Contains(Audit(rig.F), e => e.GetProperty("action").GetString() == "password-reset"
                                           && e.GetProperty("target").GetString() == "two-factor cleared");
    }

    [Fact]
    public async Task Staff_link_works_when_recovery_is_off_for_residents()
    {
        await using var rig = Factory(OffForResidents);
        var token = await StaffLinkAsync(rig);
        Assert.NotNull(Done(await ResetAsync(Client(rig), token)));
        Assert.True(await PasswordWorksAsync(rig, AnnId, NewPassword));
    }

    [Fact]
    public async Task Own_link_is_still_refused_when_recovery_is_off()
    {
        await using var rig = Factory(OffForResidents);
        var staff = await StaffLinkAsync(rig);
        // A resident's own link can't be had while their rule is off, and a staff token isn't one in disguise.
        Assert.Null(await Resets(rig).OpenAsync("not-a-token", Ct));
        Assert.True((await Resets(rig).OpenAsync(staff, Ct))!.ByStaff);
        await AskAsync(Client(rig), "Ann Smith", "ann@example.com");
        await SettledAsync(rig, 1);
        Assert.Equal(1, Actions(rig.F, "reset-refused"));
        Assert.Equal(0, Actions(rig.F, "reset-requested"));
    }

    [Fact]
    public async Task Staff_link_stops_when_the_person_becomes_staff()
    {
        await using var rig = Factory();
        var token = await StaffLinkAsync(rig);
        rig.F.Robust.SetLevel("Ann", "Smith", 100);
        Assert.Null(await Resets(rig).OpenAsync(token, Ct));
    }

    [Fact]
    public async Task Unused_staff_link_changes_nothing()
    {
        await using var rig = Factory(CodeForAll);
        Enrol(rig, ResidentStore(rig), AnnId, "Ann Smith");
        await StaffLinkAsync(rig);
        Assert.NotEqual(TwoFactorState.Off, ResidentStore(rig).StateOf(AnnId));
        Assert.True(await PasswordWorksAsync(rig, AnnId, "old-password-1"));
    }

    [Fact]
    public async Task Staff_send_keeps_the_per_account_limit()
    {
        await using var rig = Factory();
        var ann = await AnnAsync(rig);
        for (var i = 0; i < 3; i++) Assert.Equal(StaffResetResult.Sent, Resets(rig).SendByStaff(ann));
        Assert.Equal(StaffResetResult.Limited, Resets(rig).SendByStaff(ann));
    }

    [Fact]
    public async Task Second_staff_link_replaces_the_first()
    {
        await using var rig = Factory();
        var first = await StaffLinkAsync(rig);
        var second = await StaffLinkAsync(rig);
        Assert.Null(await Resets(rig).OpenAsync(first, Ct));
        Assert.NotNull(Done(await ResetAsync(Client(rig), second)));
    }

    [Fact]
    public async Task No_email_address_or_no_email()
    {
        await using var rig = Factory();
        var ann = await AnnAsync(rig);
        Assert.Equal(StaffResetResult.NoEmail, Resets(rig).SendByStaff(ann with { Email = " " }));
        await using var off = Factory(email: false);
        Assert.Equal(StaffResetResult.EmailOff, Resets(off).SendByStaff(await AnnAsync(off)));
    }
}
