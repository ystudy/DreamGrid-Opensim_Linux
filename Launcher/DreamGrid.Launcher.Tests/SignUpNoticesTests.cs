using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The hourly email that tells approvers someone is waiting (spec 2026-11-01-sign-up-approval-design §8). All names are made up.</summary>
public sealed class SignUpNoticesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SignUpNotices Notices(SignUpsTests.Rig rig) => rig.F.Services.GetRequiredService<SignUpNotices>();

    /// <summary>A grid with email, the three test staff given addresses, and Bea's sign-up waiting.</summary>
    private static async Task<SignUpsTests.Rig> WaitingAsync(SignUpApprovers who = SignUpApprovers.OwnerAndAdmins)
    {
        var rig = SignUpsTests.Factory(before: f =>
        {
            foreach (var role in new[] { StaffRole.Owner, StaffRole.Admin, StaffRole.Supervisor })
            {
                var a = TestAuth.Account(role);
                f.Robust.Add(a.FirstName, a.LastName, a.UserLevel, "pass-word1", a.PrincipalId, email: $"{a.LastName.ToLowerInvariant()}@staff.example");
            }
        });
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Bots = s.Bots with { Approvers = who } } : null!);
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), null, Ct);
        await SignUpsTests.SentAsync(rig, 1);
        return rig;
    }

    private static List<(string To, string Subject, string Text)> StaffMail(SignUpsTests.Rig rig)
    {
        lock (rig.Transport.Sent)
            return rig.Transport.Sent.Where(m => m.To.EndsWith("@staff.example", StringComparison.Ordinal))
                .Select(m => (m.To, m.Email.Subject, m.Email.Text)).ToList();
    }

    private static async Task<List<(string To, string Subject, string Text)>> StaffMailAsync(SignUpsTests.Rig rig, int atLeast)
    {
        for (var i = 0; i < 100 && StaffMail(rig).Count < atLeast; i++) await Task.Delay(25, Ct);
        return StaffMail(rig);
    }

    [Fact]
    public async Task One_email_per_hour()
    {
        await using var rig = await WaitingAsync(SignUpApprovers.Owner);

        await Notices(rig).RunAsync(Ct);
        var first = Assert.Single(await StaffMailAsync(rig, 1));
        Assert.Equal("owner@staff.example", first.To);
        Assert.Equal("1 waiting for approval on Avalon", first.Subject);

        rig.F.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(first: "Cy", email: "cy@example.com"), null, Ct);
        await Notices(rig).RunAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.Single(StaffMail(rig));

        rig.F.Time.Advance(TimeSpan.FromMinutes(61));
        await Notices(rig).RunAsync(Ct);
        var mail = await StaffMailAsync(rig, 2);
        Assert.Equal(2, mail.Count);
        Assert.Equal("2 waiting for approval on Avalon", mail[1].Subject);
        Assert.Contains("Bea Example", mail[1].Text);
        Assert.Contains("Cy Example", mail[1].Text);
    }

    [Fact]
    public async Task Gap_counts_from_the_pass_start()
    {
        await using var rig = await WaitingAsync(SignUpApprovers.Owner);
        // A pass that started two minutes ago and took that long to reach the email.
        await Notices(rig).RunAsync(Ct, startedAt: rig.F.Time.GetUtcNow() - TimeSpan.FromMinutes(2));
        await StaffMailAsync(rig, 1);

        // The next hourly pass starts 59 minutes after this one finished: a full hour after the last one started.
        rig.F.Time.Advance(TimeSpan.FromMinutes(59));
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(first: "Cy", email: "cy@example.com"), null, Ct);
        await Notices(rig).RunAsync(Ct);

        Assert.Equal(2, (await StaffMailAsync(rig, 2)).Count);
    }

    [Fact]
    public async Task No_email_without_new_waiting()
    {
        await using var rig = await WaitingAsync(SignUpApprovers.Owner);
        await Notices(rig).RunAsync(Ct);
        await StaffMailAsync(rig, 1);

        rig.F.Time.Advance(TimeSpan.FromHours(2));
        await Notices(rig).RunAsync(Ct);
        await Task.Delay(200, Ct);

        Assert.Single(StaffMail(rig));
    }

    [Theory]
    [InlineData(SignUpApprovers.Owner, new[] { "owner@staff.example" })]
    [InlineData(SignUpApprovers.OwnerAndAdmins, new[] { "admin@staff.example", "owner@staff.example" })]
    [InlineData(SignUpApprovers.OwnerAdminsAndSupervisors, new[] { "admin@staff.example", "owner@staff.example", "supervisor@staff.example" })]
    public async Task Only_approvers_get_it(SignUpApprovers who, string[] to)
    {
        await using var rig = await WaitingAsync(who);

        await Notices(rig).RunAsync(Ct);

        Assert.Equal(to, (await StaffMailAsync(rig, to.Length)).Select(m => m.To).Order());
    }

    [Fact]
    public async Task Opted_out_gets_none()
    {
        await using var rig = await WaitingAsync();
        rig.F.Services.GetRequiredService<ApprovalNotices>().SetOptedOut(TestAuth.AdminId, true);

        await Notices(rig).RunAsync(Ct);

        Assert.Equal(["owner@staff.example"], (await StaffMailAsync(rig, 1)).Select(m => m.To));
        Assert.True(rig.F.Services.GetRequiredService<ApprovalNotices>().OptedOut(TestAuth.AdminId));
    }

    [Fact]
    public async Task Unlisted_not_in_staff_email()
    {
        await using var rig = await WaitingAsync(SignUpApprovers.Owner);
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(first: "Dee", email: "ann@example.com"), null, Ct);

        await Notices(rig).RunAsync(Ct);

        var mail = Assert.Single(await StaffMailAsync(rig, 1));
        Assert.Equal("1 waiting for approval on Avalon", mail.Subject);
        Assert.DoesNotContain("Dee", mail.Text);
    }

    [Fact]
    public async Task Only_unlisted_waiting_sends_nothing()
    {
        await using var rig = SignUpsTests.Factory(before: f =>
        {
            var a = TestAuth.Account(StaffRole.Owner);
            f.Robust.Add(a.FirstName, a.LastName, a.UserLevel, "pass-word1", a.PrincipalId, email: "owner@staff.example");
        });
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), null, Ct);

        await Notices(rig).RunAsync(Ct);
        await Task.Delay(200, Ct);

        Assert.Empty(StaffMail(rig));
    }

    [Fact]
    public async Task No_applicant_email_in_message()
    {
        await using var rig = await WaitingAsync(SignUpApprovers.Owner);

        await Notices(rig).RunAsync(Ct);

        var mail = Assert.Single(await StaffMailAsync(rig, 1));
        Assert.DoesNotContain("bea@example.com", mail.Text + mail.Subject);
    }

    [Fact]
    public async Task Admin_link_is_dashboard_supervisor_link_is_sign_ups_list()
    {
        await using var rig = await WaitingAsync(SignUpApprovers.OwnerAdminsAndSupervisors);

        await Notices(rig).RunAsync(Ct);

        var mail = await StaffMailAsync(rig, 3);
        Assert.Contains("/people/sign-ups", mail.Single(m => m.To == "supervisor@staff.example").Text);
        Assert.DoesNotContain("/people/sign-ups", mail.Single(m => m.To == "admin@staff.example").Text);
    }

    [Fact]
    public async Task No_email_when_email_not_set_up()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        rig.F.Store.Save(rig.F.Store.LoadOrCreate() is var s ? s with { Bots = s.Bots with { AdminApproval = true } } : null!);
        await rig.SignUps.SubmitAsync(SignUpsTests.Form(), null, Ct);

        await Notices(rig).RunAsync(Ct);

        lock (rig.Transport.Sent) Assert.Empty(rig.Transport.Sent);
        Assert.Null(rig.F.Services.GetRequiredService<ApprovalNotices>().LastSent);
    }

    [Fact]
    public async Task Directory_down_tries_next_pass()
    {
        await using var rig = await WaitingAsync(SignUpApprovers.Owner);
        rig.F.Staff.Down = true;

        await Notices(rig).RunAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.Empty(StaffMail(rig));

        rig.F.Staff.Down = false;
        await Notices(rig).RunAsync(Ct);
        Assert.Single(await StaffMailAsync(rig, 1));
    }
}
