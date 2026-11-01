using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>A staff member's own switch for the "sign-ups waiting" email, at /profile/emails (spec 2026-11-01-sign-up-approval-design §8).</summary>
public sealed class ProfileEmailsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory(SignUpApprovers who = SignUpApprovers.OwnerAndAdmins)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Bots = s.Bots with { Approvers = who },
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    [Fact]
    public async Task Opt_out_saved()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var page = new HtmlParser().ParseDocument(await admin.GetStringAsync("/profile/emails", Ct));
        Assert.NotNull(page.QuerySelector("#SignUpsWaiting:checked"));
        Assert.Contains("Email me when new sign-ups are waiting for approval", page.Body!.TextContent);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var r = await admin.PostAsync("/profile/emails", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.True(f.Services.GetRequiredService<ApprovalNotices>().OptedOut(TestAuth.AdminId));
        page = new HtmlParser().ParseDocument(await admin.GetStringAsync("/profile/emails", Ct));
        Assert.Null(page.QuerySelector("#SignUpsWaiting:checked"));
    }

    [Fact]
    public async Task Non_approver_sees_cannot_text()
    {
        await using var f = Factory(SignUpApprovers.OwnerAndAdmins);
        var supervisor = await SignedInAsync(f, "supervisor");

        var page = new HtmlParser().ParseDocument(await supervisor.GetStringAsync("/profile/emails", Ct));

        Assert.Contains("You can't approve sign-ups on this grid.", page.Body!.TextContent);
        Assert.Null(page.QuerySelector("#SignUpsWaiting"));
    }

    [Fact]
    public async Task Nav_links_emails()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");

        var page = new HtmlParser().ParseDocument(await admin.GetStringAsync("/profile/sessions", Ct));

        Assert.NotNull(page.QuerySelector("a[href='/profile/emails']"));
    }
}
