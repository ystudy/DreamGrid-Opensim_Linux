using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Email warnings on the dashboard (full for the owner, a short line for other staff) and the recent problems list.</summary>
public sealed class EmailWarningsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ShortBanner = "Email has a problem — the grid owner can fix it on the Email page.";

    private static LauncherFactory Factory(OwnerPlan plan = OwnerPlan.Local)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(new RefusingTransport()),
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

    private static EmailAccount BrokenAccount(LauncherFactory f)
    {
        var accounts = f.Services.GetRequiredService<EmailAccounts>();
        var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
            "grid@gmail.com", null, 500, null, false, 0, null, null);
        accounts.Save(a, "pw");
        accounts.MarkProblem(a.Id, "The server refused the password", DateTimeOffset.UtcNow);
        return a;
    }

    [Fact]
    public async Task Owner_sees_warning_with_link()
    {
        await using var f = Factory();
        BrokenAccount(f);
        var owner = await SignedInAsync(f, "owner");
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/dashboard", Ct));
        var box = page.QuerySelector("#email-warnings");
        Assert.NotNull(box);
        Assert.Contains(EmailSender.WarnAttention, box.TextContent);
        Assert.Equal("/security/email", box.QuerySelector("a")?.GetAttribute("href"));
    }

    [Fact]
    public async Task Admin_sees_short_banner_without_details()
    {
        await using var f = Factory();
        BrokenAccount(f);
        var admin = await SignedInAsync(f, "admin");
        var page = new HtmlParser().ParseDocument(await admin.GetStringAsync("/dashboard", Ct));
        var box = page.QuerySelector("#email-warnings");
        Assert.NotNull(box);
        Assert.Equal(ShortBanner, box.TextContent.Trim());
        Assert.Null(box.QuerySelector("a"));
        Assert.DoesNotContain(EmailSender.WarnAttention, page.Body!.TextContent);
    }

    [Fact]
    public async Task No_banner_without_warnings_or_guest_side()
    {
        await using (var f = Factory())
        {
            var owner = await SignedInAsync(f, "owner");
            var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/dashboard", Ct));
            Assert.Null(page.QuerySelector("#email-warnings"));
        }
        await using (var f = Factory(OwnerPlan.RegionServer))
        {
            BrokenAccount(f);
            var owner = await SignedInAsync(f, "owner");
            var page = new HtmlParser().ParseDocument(await owner.GetStringAsync("/dashboard", Ct));
            Assert.Null(page.QuerySelector("#email-warnings"));
        }
    }

    [Fact]
    public async Task Recent_problems_listed_without_addresses()
    {
        await using var f = Factory();
        var accounts = f.Services.GetRequiredService<EmailAccounts>();
        accounts.Save(new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
            "grid@gmail.com", null, 500, null, false, 0, null, null), "pw");
        var sender = f.Services.GetRequiredService<EmailSender>();
        Assert.Equal(QueueResult.Queued, sender.Queue(EmailTemplates.ResetPassword, "who@nowhere.example", "Ann Avatar",
            new Dictionary<string, string> { ["AvatarName"] = "Ann Avatar", ["Link"] = "https://x.example/r?t=TOKEN", ["ExpiresIn"] = "30 minutes" }));
        await sender.DrainAsync(Ct);
        var owner = await SignedInAsync(f, "owner");
        var html = await owner.GetStringAsync("/security/email", Ct);
        var row = new HtmlParser().ParseDocument(html).QuerySelector("#email-problems tbody tr");
        Assert.NotNull(row);
        Assert.Contains("Reset your password", row.TextContent);
        Assert.Contains("Ann Avatar", row.TextContent);
        Assert.Contains(EmailSender.Refused, row.TextContent);
        Assert.DoesNotContain("who@nowhere.example", html);
        Assert.DoesNotContain("TOKEN", html);
    }

    private sealed class RefusingTransport : IMailTransport
    {
        public Task<SendOutcome> SendAsync(EmailAccount account, string password, string to, RenderedEmail email, CancellationToken ct) =>
            Task.FromResult(new SendOutcome(SendResult.RecipientRefused, "The server refused this address"));
    }
}
