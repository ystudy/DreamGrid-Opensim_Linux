using System.Net;
using System.Text.Json;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Sign-up states and the approve and deny service (spec 2026-11-01-sign-up-approval-design §3–§5). All names are made up.</summary>
public sealed class SignUpApprovalsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IPAddress Caller = IPAddress.Parse("192.168.1.61");
    private const string Staff = "Test Owner";

    private static SignUpApprovals Approvals(SignUpsTests.Rig rig) => rig.F.Services.GetRequiredService<SignUpApprovals>();

    private static void Set(SignUpsTests.Rig rig, Func<BotSettings, BotSettings> bots) =>
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Bots = bots(s.Bots) } : null!);

    private static async Task<(SignUpsTests.Rig Rig, Guid Bea)> SignedUpAsync(bool email = true, bool approval = false)
    {
        var rig = SignUpsTests.Factory(email: email);
        if (approval) Set(rig, b => b with { AdminApproval = true });
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct);
        return (rig, rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId);
    }

    private static int Level(SignUpsTests.Rig rig, Guid id) => rig.F.Robust.All.Single(a => a.PrincipalId == id).UserLevel;

    private static string AuditText(SignUpsTests.Rig rig)
    {
        var path = Path.Combine(rig.F.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private static List<string> Actions(SignUpsTests.Rig rig) => AuditText(rig).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString()!).ToList();

    // States made by sign-up.

    [Fact]
    public async Task With_email_waits_for_email()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;

        Assert.Equal(SignUpState.WaitingForEmail, rig.Pending.Get(bea)!.State);
        Assert.Equal("Bea Example", rig.Pending.Get(bea)!.Name);
    }

    [Fact]
    public async Task Decoy_is_unlisted()
    {
        await using var rig = SignUpsTests.Factory();
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), Caller, Ct);
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId;

        Assert.Equal(SignUpState.Unlisted, rig.Pending.Get(bea)!.State);
        Assert.Empty(rig.Pending.Shown());
    }

    [Fact]
    public async Task No_email_switch_on_waits_for_staff()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        Set(rig, b => b with { AdminApproval = true });

        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form(), Caller, Ct);

        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId;
        Assert.Equal(SignUpOutcome.WaitingForStaff, r.Outcome);
        Assert.Null(r.Account);
        Assert.Equal(-1, Level(rig, bea));
        Assert.Equal(SignUpState.WaitingForStaff, rig.Pending.Get(bea)!.State);
    }

    [Fact]
    public async Task No_email_switch_off_lets_in()
    {
        var (rig, bea) = await SignedUpAsync(email: false);
        await using var _ = rig;

        Assert.Equal(0, Level(rig, bea));
        Assert.Equal(SignUpState.LetIn, rig.Pending.Get(bea)!.State);
        Assert.False(rig.Pending.Contains(bea));
    }

    [Fact]
    public async Task Resend_does_not_extend_made()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        var made = rig.Pending.Get(bea)!.Made;
        rig.F.Time.Advance(TimeSpan.FromHours(2));

        await rig.SignUps.ResendAsync("Bea", "Example", Caller, Ct);

        Assert.Equal(made, rig.Pending.Get(bea)!.Made);
        Assert.Equal(rig.F.Time.GetUtcNow(), rig.Pending.Get(bea)!.LastLink);
    }

    [Fact]
    public async Task Confirm_marks_self_approved()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);

        var (result, account) = await rig.SignUps.ConfirmAsync(token, Caller, Ct);

        Assert.Equal(ConfirmResult.Confirmed, result);
        Assert.Equal(bea, account!.PrincipalId);
        Assert.Equal(SignUpState.SelfApproved, rig.Pending.Get(bea)!.State);
    }

    [Fact]
    public async Task Confirm_after_staff_approval_is_already_approved()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        Assert.Equal(ApprovalResult.Done, (await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct)).Result);

        var (result, _) = await rig.SignUps.ConfirmAsync(token, Caller, Ct);

        Assert.Equal(ConfirmResult.AlreadyApproved, result);
        Assert.Equal(SignUpState.Approved, rig.Pending.Get(bea)!.State);
        Assert.Equal(ConfirmResult.Bad, (await rig.SignUps.ConfirmAsync(token, Caller, Ct)).Result);
    }

    [Fact]
    public async Task Confirm_unlisted_upgraded_entry_still_works()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        rig.Pending.Add(bea, "", SignUpState.Unlisted);

        var (result, account) = await rig.SignUps.ConfirmAsync(token, Caller, Ct);

        Assert.Equal(ConfirmResult.Confirmed, result);
        Assert.Equal(0, account!.UserLevel);
        // An unlisted entry never shows on the board, not even once it is confirmed.
        Assert.Empty(rig.Pending.Shown());
        Assert.False(rig.Pending.Contains(bea));
    }

    [Fact]
    public async Task Approve_refused_when_address_now_used_by_another_account()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        await SignUpsTests.SentAsync(rig, 1);
        rig.F.Robust.Add("Cal", "Other", 0, "pass-word1", email: "Bea@Example.com ");

        var r = await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct);

        Assert.Equal(ApprovalResult.EmailTaken, r.Result);
        Assert.Equal(SignUpApprovals.EmailTakenText, r.Message);
        Assert.Equal(-1, Level(rig, bea));
        Assert.Equal(SignUpState.WaitingForEmail, rig.Pending.Get(bea)!.State);
    }

    [Fact]
    public async Task Approve_audited_even_when_board_write_fails()
    {
        var (rig, bea) = await SignedUpAsync(email: false, approval: true);
        await using var _ = rig;
        var file = Path.Combine(rig.F.Paths.Guests, "pending-signups.json");
        FileStream? held = null;
        var inner = rig.F.Grid.OnSetUserLevel;
        // The board file is locked (antivirus, backup) between the level change and the board write.
        rig.F.Grid.OnSetUserLevel = (first, last, level) =>
        {
            held ??= new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return inner?.Invoke(first, last, level);
        };

        ApprovalOutcome r;
        try { r = await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct); }
        finally { held?.Dispose(); }

        Assert.Equal(ApprovalResult.Done, r.Result);
        Assert.Equal(0, Level(rig, bea));
        Assert.Contains("signup-approved", Actions(rig));
    }

    // Approve.

    [Fact]
    public async Task Approve_raises_level_and_sends_email()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        await SignUpsTests.SentAsync(rig, 1);

        var r = await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct);

        Assert.Equal(ApprovalResult.Done, r.Result);
        Assert.Equal(0, Level(rig, bea));
        var e = rig.Pending.Get(bea)!;
        Assert.Equal((SignUpState.Approved, Staff), (e.State, e.By));
        var sent = await SignUpsTests.SentAsync(rig, 2);
        Assert.Equal("bea@example.com", sent[1].To);
        Assert.Contains("/guest/sign-in", sent[1].Email.Text);
        Assert.Contains("signup-approved", Actions(rig));
    }

    [Fact]
    public async Task Approve_without_email_sends_nothing()
    {
        var (rig, bea) = await SignedUpAsync(email: false, approval: true);
        await using var _ = rig;

        Assert.Equal(ApprovalResult.Done, (await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Admin, Caller, Ct)).Result);

        Assert.Equal(0, Level(rig, bea));
        await Task.Delay(200, Ct);
        lock (rig.Transport.Sent) Assert.Empty(rig.Transport.Sent);
    }

    [Fact]
    public async Task Approve_twice_already_done_by()
    {
        var (rig, bea) = await SignedUpAsync(email: false, approval: true);
        await using var _ = rig;
        await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct);

        var r = await Approvals(rig).ApproveAsync(bea, "Other Admin", StaffRole.Admin, Caller, Ct);

        Assert.Equal(ApprovalResult.AlreadyDone, r.Result);
        Assert.Equal("Already done by Test Owner.", r.Message);
        Assert.Single(Actions(rig), a => a == "signup-approved");
    }

    [Fact]
    public async Task Approve_and_confirm_race_one_wins()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        var token = SignUpsTests.LinkToken(Assert.Single(await SignUpsTests.SentAsync(rig, 1)).Email);
        using var gate = new ManualResetEventSlim(false);
        var inner = rig.F.Grid.OnSetUserLevel;
        rig.F.Grid.OnSetUserLevel = (first, last, level) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return inner?.Invoke(first, last, level);
        };

        var approve = Task.Run(() => Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct), Ct);
        var confirm = Task.Run(() => rig.SignUps.ConfirmAsync(token, Caller, Ct), Ct);
        await Task.Delay(200, Ct);
        gate.Set();
        var (a, c) = (await approve, await confirm);

        Assert.Single(rig.F.Grid.Snapshot(), call => call == "SetUserLevel Bea Example 0");
        var approvedWon = a.Result == ApprovalResult.Done;
        Assert.Equal(approvedWon ? ConfirmResult.AlreadyApproved : ConfirmResult.Confirmed, c.Result);
        Assert.Equal(approvedWon ? SignUpState.Approved : SignUpState.SelfApproved, rig.Pending.Get(bea)!.State);
        Assert.Equal(1, Actions(rig).Count(x => x is "signup-approved" or "signup-confirmed"));
    }

    [Fact]
    public async Task Unlisted_cannot_be_approved()
    {
        await using var rig = SignUpsTests.Factory();
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), Caller, Ct);
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea").PrincipalId;

        Assert.Equal(ApprovalResult.NotFound, (await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct)).Result);
        Assert.Equal(ApprovalResult.NotFound, (await Approvals(rig).DenyAsync(bea, Staff, StaffRole.Owner, null, Caller, Ct)).Result);

        Assert.Equal(-1, Level(rig, bea));
        Assert.Equal(SignUpState.Unlisted, rig.Pending.Get(bea)!.State);
    }

    [Theory]
    [InlineData(SignUpApprovers.Owner, StaffRole.Admin)]
    [InlineData(SignUpApprovers.OwnerAndAdmins, StaffRole.Supervisor)]
    [InlineData(SignUpApprovers.OwnerAdminsAndSupervisors, StaffRole.None)]
    public async Task Not_allowed_role_refused_nothing_changes(SignUpApprovers who, StaffRole role)
    {
        var (rig, bea) = await SignedUpAsync(email: false, approval: true);
        await using var _ = rig;
        Set(rig, b => b with { Approvers = who });

        Assert.Equal(ApprovalResult.NotAllowed, (await Approvals(rig).ApproveAsync(bea, "Some One", role, Caller, Ct)).Result);
        Assert.Equal(ApprovalResult.NotAllowed, (await Approvals(rig).DenyAsync(bea, "Some One", role, null, Caller, Ct)).Result);

        Assert.Equal(-1, Level(rig, bea));
        Assert.Equal(SignUpState.WaitingForStaff, rig.Pending.Get(bea)!.State);
    }

    [Fact]
    public async Task Approve_level_failure_stays_waiting()
    {
        var (rig, bea) = await SignedUpAsync(email: false, approval: true);
        await using var _ = rig;
        rig.F.Grid.SetUserLevelError = new HttpRequestException("Connection refused");

        var r = await Approvals(rig).ApproveAsync(bea, Staff, StaffRole.Owner, Caller, Ct);

        Assert.Equal((ApprovalResult.Busy, "Couldn't approve right now. Try again in a few minutes."), (r.Result, r.Message));
        Assert.Equal(SignUpState.WaitingForStaff, rig.Pending.Get(bea)!.State);
    }

    // Deny.

    [Fact]
    public async Task Deny_deletes_and_emails_note()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        await SignUpsTests.SentAsync(rig, 1);

        var r = await Approvals(rig).DenyAsync(bea, Staff, StaffRole.Owner, "  This grid is for the club only.  ", Caller, Ct);

        Assert.Equal(ApprovalResult.Done, r.Result);
        Assert.DoesNotContain(rig.F.Robust.All, a => a.PrincipalId == bea);
        Assert.Equal((SignUpState.Denied, Staff), (rig.Pending.Get(bea)!.State, rig.Pending.Get(bea)!.By));
        Assert.False(rig.Pending.Contains(bea));
        var sent = await SignUpsTests.SentAsync(rig, 2);
        Assert.Equal("bea@example.com", sent[1].To);
        Assert.Contains("wasn't approved", sent[1].Email.Text);
        Assert.Contains("This grid is for the club only.", sent[1].Email.Text);
        Assert.Contains("signup-denied", Actions(rig));
    }

    [Fact]
    public async Task Deny_without_note_email_has_no_reason()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        await SignUpsTests.SentAsync(rig, 1);

        await Approvals(rig).DenyAsync(bea, Staff, StaffRole.Owner, null, Caller, Ct);

        var text = (await SignUpsTests.SentAsync(rig, 2))[1].Email.Text;
        Assert.EndsWith("wasn't approved.", text.TrimEnd());
    }

    [Fact]
    public async Task Deny_note_not_audited()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;

        await Approvals(rig).DenyAsync(bea, Staff, StaffRole.Owner, "Secret reason words", Caller, Ct);

        Assert.DoesNotContain("Secret reason words", AuditText(rig));
    }

    [Fact]
    public async Task Deny_delete_failure_stays_waiting_no_email()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        await SignUpsTests.SentAsync(rig, 1);
        rig.F.Remover.Down = true;

        var r = await Approvals(rig).DenyAsync(bea, Staff, StaffRole.Owner, "note", Caller, Ct);

        Assert.Equal((ApprovalResult.Busy, "Couldn't deny right now. Try again in a few minutes."), (r.Result, r.Message));
        Assert.Equal(SignUpState.WaitingForEmail, rig.Pending.Get(bea)!.State);
        await Task.Delay(200, Ct);
        lock (rig.Transport.Sent) Assert.Single(rig.Transport.Sent);
    }

    [Fact]
    public async Task Deny_note_cut_at_500()
    {
        var (rig, bea) = await SignedUpAsync();
        await using var _ = rig;
        await SignUpsTests.SentAsync(rig, 1);

        await Approvals(rig).DenyAsync(bea, Staff, StaffRole.Owner, new string('a', 499) + "bcd", Caller, Ct);

        var text = (await SignUpsTests.SentAsync(rig, 2))[1].Email.Text;
        Assert.Contains(new string('a', 499) + "b", text);
        Assert.DoesNotContain("bc", text);
    }
}
