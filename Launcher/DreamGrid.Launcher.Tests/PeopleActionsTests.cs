using System.Security.Claims;
using System.Text.Json;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The People page's tasks on the real host's services (spec 2026-11-03-people-admin-design).</summary>
public sealed class PeopleActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid GailId = Guid.Parse("0b000000-0000-0000-0000-00000000000b");
    private static readonly Guid RegionId = Guid.Parse("0c000000-0000-0000-0000-00000000000c");

    private sealed record Rig(LauncherFactory F, EmailPageTests.FakeTransport Transport) : IAsyncDisposable
    {
        public PeopleActions Actions => F.Services.GetRequiredService<PeopleActions>();
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static Rig Factory()
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Gail", "Guest", 0, "pass-word1", GailId, email: "gail@example.com");
        var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
            "grid@gmail.com", null, 500, null, false, 0, null, null);
        f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        return new Rig(f, transport);
    }

    private static ClaimsPrincipal As(StaffRole role)
    {
        var a = TestAuth.Account(role);
        List<Claim> claims =
        [
            new(ClaimTypes.Name, $"{a.FirstName} {a.LastName}"),
            new(StaffSignIn.PrincipalClaim, a.PrincipalId.ToString()),
            .. StaffSignIn.RoleClaims(role).Select(r => new Claim(ClaimTypes.Role, r)),
            new(StaffSignIn.SessionClaim, "test"),
            new(StaffSignIn.StampClaim, "test"),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Admin));
    }

    private static Core.OpenSim.RobustAccount Gail(Rig rig) => rig.F.Robust.All.Single(a => a.PrincipalId == GailId);

    private static async Task<List<(string To, string Subject)>> SentAsync(Rig rig, int atLeast)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (rig.Transport.Sent)
                if (rig.Transport.Sent.Count >= atLeast) return [.. rig.Transport.Sent.Select(m => (m.To, m.Email.Subject))];
            await Task.Delay(25, Ct);
        }
        lock (rig.Transport.Sent) return [.. rig.Transport.Sent.Select(m => (m.To, m.Email.Subject))];
    }

    private static List<JsonElement> Audit(LauncherFactory f, string action)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("action").GetString() == action).ToList();
    }

    [Fact]
    public async Task Rename_writes_holds_old_name_emails_and_audits()
    {
        await using var rig = Factory();
        var r = await rig.Actions.EditAsync(As(StaffRole.Admin), "10.0.0.1", GailId, "Gwen", "Guest", "gail@example.com", Ct);

        Assert.True(r.Done, r.Message);
        Assert.Equal(("Gwen", "Guest"), (Gail(rig).FirstName, Gail(rig).LastName));
        Assert.True(rig.F.Services.GetRequiredService<HeldNames>().IsHeld("Gail Guest"));
        Assert.Contains(("gail@example.com", "Your name on Avalon was changed"), await SentAsync(rig, 1));
        Assert.Equal("Gail Guest -> Gwen Guest", Assert.Single(Audit(rig.F, "name-changed")).GetProperty("target").GetString());
    }

    [Fact]
    public async Task Rename_to_a_taken_name_is_refused()
    {
        await using var rig = Factory();
        var r = await rig.Actions.EditAsync(As(StaffRole.Admin), null, GailId, "test", "ADMIN", "gail@example.com", Ct);
        Assert.False(r.Done);
        Assert.Equal("That name is taken. Try another.", r.Message);
        Assert.Equal("Gail", Gail(rig).FirstName);

        rig.F.Services.GetRequiredService<HeldNames>().Hold("Held Name", Guid.NewGuid());
        Assert.Equal("That name is taken. Try another.",
            (await rig.Actions.EditAsync(As(StaffRole.Admin), null, GailId, "Held", "Name", "gail@example.com", Ct)).Message);
    }

    [Fact]
    public async Task Rename_while_online_is_refused()
    {
        await using var rig = Factory();
        rig.F.People.Online[GailId] = RegionId;
        var r = await rig.Actions.EditAsync(As(StaffRole.Admin), null, GailId, "Gwen", "Guest", "gail@example.com", Ct);
        Assert.Equal("Sign them out first: they are in-world.", r.Message);
        Assert.Equal("Gail", Gail(rig).FirstName);
    }

    [Fact]
    public async Task Email_change_tells_old_and_new_address()
    {
        await using var rig = Factory();
        var r = await rig.Actions.EditAsync(As(StaffRole.Admin), null, GailId, "Gail", "Guest", " New@Example.com ", Ct);

        Assert.True(r.Done, r.Message);
        Assert.Equal("new@example.com", Gail(rig).Email);
        var sent = await SentAsync(rig, 2);
        Assert.Contains(("gail@example.com", "Your email address on Avalon was changed"), sent);
        Assert.Contains(("new@example.com", "Your email address on Avalon"), sent);
        var audit = Assert.Single(Audit(rig.F, "email-changed"));
        Assert.Equal("Gail Guest", audit.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Email_in_use_by_another_account_is_refused()
    {
        await using var rig = Factory();
        rig.F.Robust.Add("Other", "Person", 0, "pass-word1", email: "taken@example.com");
        var r = await rig.Actions.EditAsync(As(StaffRole.Admin), null, GailId, "Gail", "Guest", "TAKEN@example.com", Ct);
        Assert.False(r.Done);
        Assert.Equal("That email address is used by another account.", r.Message);
        Assert.Equal("gail@example.com", Gail(rig).Email);
    }

    [Fact]
    public async Task Clearing_email_tells_the_old_address()
    {
        await using var rig = Factory();
        var r = await rig.Actions.EditAsync(As(StaffRole.Admin), null, GailId, "Gail", "Guest", "", Ct);
        Assert.True(r.Done, r.Message);
        Assert.Equal("", Gail(rig).Email);
        Assert.Equal([("gail@example.com", "Your email address on Avalon was changed")], await SentAsync(rig, 1));
    }

    [Fact]
    public async Task Sign_out_kicks_when_online()
    {
        await using var rig = Factory();
        rig.F.People.Online[GailId] = RegionId;
        var stamps = rig.F.Services.GetRequiredService<GuestStamps>();
        var before = stamps.StampOf(GailId);

        var r = await rig.Actions.SignOutAsync(As(StaffRole.Admin), null, GailId, Ct);

        Assert.True(r.Done, r.Message);
        Assert.Contains($"Kick {RegionId} Gail Guest", rig.F.Grid.Snapshot());
        Assert.NotEqual(before, stamps.StampOf(GailId));
        Assert.Equal("ok", Assert.Single(Audit(rig.F, "signed-out")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Sign_out_when_kick_fails_says_so()
    {
        await using var rig = Factory();
        rig.F.People.Online[GailId] = RegionId;
        rig.F.Grid.KickError = new InvalidOperationException("There is no such region.");

        var r = await rig.Actions.SignOutAsync(As(StaffRole.Admin), null, GailId, Ct);

        Assert.Equal("Signed out of the website. They are still in-world: the region they are in couldn't be reached.", r.Message);
        Assert.Equal("failed", Assert.Single(Audit(rig.F, "signed-out")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Suspend_also_kicks()
    {
        await using var rig = Factory();
        rig.F.People.Online[GailId] = RegionId;
        var r = await rig.Actions.SuspendAsync(As(StaffRole.Admin), null, GailId, Ct);
        Assert.True(r.Done, r.Message);
        Assert.Contains("SetUserLevel Gail Guest -1", rig.F.Grid.Snapshot());
        Assert.Contains($"Kick {RegionId} Gail Guest", rig.F.Grid.Snapshot());
    }

    [Fact]
    public async Task Send_reset_audits()
    {
        await using var rig = Factory();
        var r = await rig.Actions.SendResetAsync(As(StaffRole.Admin), null, GailId, Ct);
        Assert.True(r.Done, r.Message);
        Assert.Equal("A reset link was sent to Gail Guest.", r.Message);
        Assert.Equal("ok", Assert.Single(Audit(rig.F, "reset-sent")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Supervisor_posting_edit_is_refused_and_audited()
    {
        await using var rig = Factory();
        var r = await rig.Actions.EditAsync(As(StaffRole.Supervisor), null, GailId, "Gwen", "Guest", "gail@example.com", Ct);
        Assert.False(r.Done);
        Assert.Equal(PeopleRules.SupervisorOnly, r.Message);
        Assert.Equal("refused", Assert.Single(Audit(rig.F, "name-changed")).GetProperty("result").GetString());
        Assert.Equal("Gail", Gail(rig).FirstName);
    }

    [Fact]
    public async Task Nothing_changed()
    {
        await using var rig = Factory();
        var r = await rig.Actions.EditAsync(As(StaffRole.Admin), null, GailId, "Gail", "Guest", "GAIL@example.com", Ct);
        Assert.False(r.Done);
        Assert.Equal("Nothing was changed.", r.Message);
    }

    [Fact]
    public async Task Someone_gone_is_a_message()
    {
        await using var rig = Factory();
        var r = await rig.Actions.SignOutAsync(As(StaffRole.Admin), null, Guid.NewGuid(), Ct);
        Assert.Equal(PeopleActions.Gone, r.Message);
    }
}
