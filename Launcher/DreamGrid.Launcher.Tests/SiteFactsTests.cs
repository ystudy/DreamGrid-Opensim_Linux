using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The cookie list the owner is shown matches the cookies the site really sets.</summary>
public sealed class SiteFactsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SetupKey = "tok-1234567890abcdefghijklmnopqrstuv";

    /// <summary>Writes down the name of every cookie a response sets (a deleting header counts), then passes the response on.</summary>
    private sealed class Recorder(HashSet<string> seen) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var headers))
                foreach (var header in headers)
                    seen.Add(header.Split('=')[0]);
            return response;
        }
    }

    private static HttpClient Client(LauncherFactory f, HashSet<string> seen) =>
        f.CreateDefaultClient(new CookieContainerHandler(), new Recorder(seen));

    private static LauncherFactory AfterSetup()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string door, string name)
    {
        var form = new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Password"] = "pass-word1",
            ["__RequestVerificationToken"] = await TokenAsync(client, door),
        };
        return await client.PostAsync(door, new FormUrlEncodedContent(form), Ct);
    }

    [Fact]
    public async Task Every_cookie_set_is_listed()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // dg-setup: the setup link, before the owner exists.
        await using (var f = new LauncherFactory(s => s with { SetupToken = SetupKey, Plan = null, Setup = s.Setup with { NetworkSet = true } })
        {
            Before = TestAuth.Apply,
        })
        {
            var client = Client(f, seen);
            using var link = await client.GetAsync("/setup?token=" + SetupKey, Ct);
            Assert.Equal(HttpStatusCode.Redirect, link.StatusCode);
        }

        // dg-af (a form's GET) and dg-auth (a staff password sign-in).
        await using (var f = AfterSetup())
        {
            var client = Client(f, seen);
            using var r = await SignInAsync(client, "/login", "Test Admin");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/", r.Headers.Location?.OriginalString);
        }

        // dg-2fa: the password was right and the account has two-factor on.
        await using (var f = AfterSetup())
        {
            var admin = TestAuth.Account(StaffRole.Admin);
            var store = f.Services.GetRequiredService<TwoFactorStore>();
            var (secret, _) = store.BeginEnrol(admin.PrincipalId, "Test Admin", "Avalon");
            Assert.NotNull(store.ConfirmEnrol(admin.PrincipalId, Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())), secret));
            f.Time.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
            var client = Client(f, seen);
            using var r = await SignInAsync(client, "/login", "Test Admin");
            Assert.Equal("/login/code", r.Headers.Location?.OriginalString.Split('?')[0]);
        }

        // dg-guest: the guest door.
        await using (var f = AfterSetup())
        {
            var client = Client(f, seen);
            using var r = await SignInAsync(client, "/guest/sign-in", "Test Guest");
            Assert.Equal("/guest", r.Headers.Location?.OriginalString);
        }

        // dg-notice: a form that answers with a one-time message (the legal editor's save).
        await using (var f = AfterSetup())
        {
            var client = Client(f, seen);
            using (await client.GetAsync("/__test/signin?role=owner", Ct)) { }
            var form = new Dictionary<string, string>
            {
                ["Texts[terms]"] = "Test terms one.",
                ["__RequestVerificationToken"] = await TokenAsync(client, "/security/legal"),
            };
            using var r = await client.PostAsync("/security/legal?handler=Save", new FormUrlEncodedContent(form), Ct);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }

        var listed = SiteFacts.Cookies.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(seen.Except(listed)); // every cookie the site set is listed
        Assert.Empty(listed.Except(seen)); // and every listed cookie was seen
    }

    [Fact]
    public void Every_fact_has_words_and_no_cookie_is_listed_twice()
    {
        Assert.All(SiteFacts.Cookies, c => Assert.False(string.IsNullOrWhiteSpace(c.Purpose)));
        Assert.Equal(SiteFacts.Cookies.Count, SiteFacts.Cookies.Select(c => c.Name).Distinct().Count());
        Assert.All(SiteFacts.Stored, s => Assert.False(string.IsNullOrWhiteSpace(s.What) || string.IsNullOrWhiteSpace(s.Where)));
    }
}
