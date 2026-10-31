using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The guest door: /guest/sign-in, its messages, its audit lines, its throttle and the level recheck.</summary>
public sealed class GuestSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Wrong = "Name or password is wrong.";
    private const string Locked = "Too many attempts. Try again in a few minutes.";
    private const string RobustDown = "The grid's sign-in service isn't running right now. Try again later.";

    private static LauncherFactory Factory(Action<IApplicationBuilder>? before = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = before ?? TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Gail", "Guest", 50, "pass-word1", Guid.Parse("0b000000-0000-0000-0000-00000000000b"));
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string name, string password)
    {
        var page = await client.GetStringAsync("/guest/sign-in", Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        }), Ct);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector(".error")?.TextContent.Trim();

    private static string? LocationPath(HttpResponseMessage r) =>
        r.Headers.Location is { IsAbsoluteUri: true } u ? u.AbsolutePath : r.Headers.Location?.OriginalString.Split('?')[0];

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task Level_zero_guest_signs_in_and_lands_on_guest_home()
    {
        await using var f = Factory();
        var client = Client(f);

        using var r = await SignInAsync(client, "Test Guest", "pass-word1");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest", r.Headers.Location?.OriginalString);
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-guest=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase); // a session cookie
        using var home = await client.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("You're signed in as Test Guest.", await home.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_staff_member_signs_in_with_the_password_alone_and_gets_only_the_guest_pages()
    {
        await using var f = Factory();
        var client = Client(f);

        using var r = await SignInAsync(client, "Test Admin", "pass-word1");

        Assert.Equal("/guest", r.Headers.Location?.OriginalString);
        using var home = await client.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        using var logs = await client.GetAsync("/logs", Ct);
        Assert.Equal("/login", LocationPath(logs));
    }

    [Fact]
    public async Task Suspended_account_refused_with_generic_message()
    {
        await using var f = Factory();
        f.Robust.SetLevel("Gail", "Guest", -1);

        using var suspended = await SignInAsync(Client(f), "Gail Guest", "pass-word1");
        using var wrongPassword = await SignInAsync(Client(f), "Test Guest", "not-the-password");
        using var unknown = await SignInAsync(Client(f), "Nobody Here", "pass-word1");
        using var noLast = await SignInAsync(Client(f), "Madonna", "pass-word1");

        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal(Wrong, await ErrorAsync(suspended));
        Assert.Equal(Wrong, await ErrorAsync(wrongPassword));
        Assert.Equal(Wrong, await ErrorAsync(unknown));
        Assert.Equal(Wrong, await ErrorAsync(noLast));
        Assert.False(suspended.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("dg-guest=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Guest_door_failures_lock_the_name_only_at_the_guest_door_but_count_toward_the_address()
    {
        var remote = IPAddress.Parse("10.1.2.3");
        await using var f = Factory(app =>
        {
            app.Use((ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = remote;
                return next(ctx);
            });
            TestAuth.Apply(app);
        });
        var client = Client(f);
        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
        {
            using var r = await SignInAsync(client, "Test Admin", "bad-password");
            Assert.Equal(Wrong, await ErrorAsync(r));
        }
        var lookups = f.Robust.Lookups;

        // The guest door locks the name there...
        using (var guestDoor = await SignInAsync(client, "test admin", "pass-word1"))
            Assert.Equal(Locked, await ErrorAsync(guestDoor));
        Assert.Equal(lookups, f.Robust.Lookups);
        // ...but not at the staff door, which keeps its own count for each name.
        using (var staffDoor = await StaffSignInAsync(Client(f), "Test Admin", "pass-word1"))
        {
            Assert.Equal(HttpStatusCode.Redirect, staffDoor.StatusCode);
            Assert.NotEqual("/login/code", LocationPath(staffDoor));
        }

        // The address's count is shared by both doors: the five guest-door failures and 15 more at the staff door lock it.
        for (var i = 0; i < LoginThrottle.IpMaxFailures - LoginThrottle.NameMaxFailures; i++)
        {
            using var r = await StaffSignInAsync(client, $"Guess{i} Person", "bad-password");
            Assert.NotEqual(Locked, await ErrorAsync(r));
        }
        using (var staff = await StaffSignInAsync(Client(f), "Test Owner", "pass-word1"))
            Assert.Equal(Locked, await ErrorAsync(staff));
        using (var guest = await SignInAsync(Client(f), "Test Guest", "pass-word1"))
            Assert.Equal(Locked, await ErrorAsync(guest));
    }

    private static async Task<HttpResponseMessage> StaffSignInAsync(HttpClient client, string name, string password)
    {
        var login = new HtmlParser().ParseDocument(await client.GetStringAsync("/login", Ct));
        return await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Password"] = password,
            ["__RequestVerificationToken"] = login.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        }), Ct);
    }

    [Fact]
    public async Task Robust_down_shows_service_message_and_counts_nothing()
    {
        await using var f = Factory();
        f.Robust.Down = true;
        var client = Client(f);
        for (var i = 0; i < 6; i++)
        {
            using var r = await SignInAsync(client, "Test Guest", "pass-word1");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(RobustDown, await ErrorAsync(r));
        }
        f.Robust.Down = false;
        f.Robust.FailureReply = true;
        using (var odd = await SignInAsync(client, "Test Guest", "pass-word1"))
            Assert.Equal(RobustDown, await ErrorAsync(odd));
        f.Robust.FailureReply = false;

        using var ok = await SignInAsync(client, "Test Guest", "pass-word1");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [Fact]
    public async Task Sign_in_is_audited_as_guest()
    {
        await using var f = Factory();
        using (await SignInAsync(Client(f), "Test Guest", "pass-word1")) { }
        using (await SignInAsync(Client(f), "Test Guest", "secret-pass-9")) { }
        using (await SignInAsync(Client(f), "my-secret-pw", "secret-pass-9")) { } // a password typed into the name field

        var entries = Audit(f);
        var ok = Assert.Single(entries, e => e.GetProperty("action").GetString() == "sign-in");
        Assert.Equal("Test Guest", ok.GetProperty("actor").GetString());
        Assert.Equal("guest", ok.GetProperty("role").GetString());
        Assert.Equal(TestAuth.GuestId.ToString(), ok.GetProperty("actorId").GetString());
        Assert.Equal("ok", ok.GetProperty("result").GetString());

        var refused = entries.Where(e => e.GetProperty("action").GetString() == "sign-in-refused").ToList();
        Assert.Equal(2, refused.Count);
        Assert.Equal("Test", refused[0].GetProperty("actor").GetString()); // the first name only
        Assert.Equal("(an incomplete name)", refused[1].GetProperty("actor").GetString());
        Assert.All(refused, e => Assert.Equal("refused", e.GetProperty("result").GetString()));
        var text = string.Join('\n', entries.Select(e => e.GetRawText()));
        Assert.DoesNotContain("pass-word1", text);
        Assert.DoesNotContain("secret-pass-9", text);
        Assert.DoesNotContain("my-secret-pw", text);
        Assert.DoesNotContain("Guest\"", string.Join('\n', refused.Select(e => e.GetRawText())));
        Assert.DoesNotContain(f.Logs, l => l.Message.Contains("secret-pass-9") || l.Message.Contains("pass-word1") || l.Message.Contains("my-secret-pw"));
    }

    [Fact]
    public async Task Level_recheck_signs_out_suspended_guest()
    {
        await using var f = Factory();
        var client = Client(f);
        using (await SignInAsync(client, "Test Guest", "pass-word1")) { }
        f.Robust.SetLevel("Test", "Guest", -1);

        // Within five minutes the level is not read again.
        f.Time.Advance(TimeSpan.FromMinutes(4));
        using (var still = await client.GetAsync("/guest", Ct))
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        f.Time.Advance(TimeSpan.FromMinutes(2));
        using var r = await client.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/sign-in", LocationPath(r));
        using var again = await client.GetAsync("/guest", Ct);
        Assert.Equal("/guest/sign-in", LocationPath(again));
    }

    [Fact]
    public async Task Level_recheck_signs_out_a_deleted_guest()
    {
        await using var f = Factory();
        var client = Client(f);
        using (await SignInAsync(client, "Test Guest", "pass-word1")) { }
        f.Robust.Remove("Test", "Guest");

        f.Time.Advance(TimeSpan.FromMinutes(5));

        using var r = await client.GetAsync("/guest", Ct);
        Assert.Equal("/guest/sign-in", LocationPath(r));
    }

    [Fact]
    public async Task Level_recheck_keeps_the_session_when_robust_does_not_answer()
    {
        await using var f = Factory();
        var client = Client(f);
        using (await SignInAsync(client, "Test Guest", "pass-word1")) { }
        f.Robust.Down = true;

        f.Time.Advance(TimeSpan.FromMinutes(5));
        using (var down = await client.GetAsync("/guest", Ct))
            Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        var lookups = f.Robust.Lookups;
        // After a failure it is asked again only a minute later.
        f.Time.Advance(TimeSpan.FromSeconds(30));
        using (var soon = await client.GetAsync("/guest", Ct))
            Assert.Equal(HttpStatusCode.OK, soon.StatusCode);
        Assert.Equal(lookups, f.Robust.Lookups);

        f.Robust.Down = false;
        f.Robust.FailureReply = true;
        f.Time.Advance(TimeSpan.FromMinutes(1));
        using (var odd = await client.GetAsync("/guest", Ct))
            Assert.Equal(HttpStatusCode.OK, odd.StatusCode);
        Assert.Equal(lookups + 1, f.Robust.Lookups); // it was asked, and the odd reply changed nothing
    }

    [Fact]
    public async Task Level_recheck_with_the_same_level_renews_the_check()
    {
        await using var f = Factory();
        var client = Client(f);
        using (await SignInAsync(client, "Test Guest", "pass-word1")) { }
        var lookups = f.Robust.Lookups;

        f.Time.Advance(TimeSpan.FromMinutes(5));
        using (var r = await client.GetAsync("/guest", Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(lookups + 1, f.Robust.Lookups);

        f.Time.Advance(TimeSpan.FromMinutes(1));
        using (var r = await client.GetAsync("/guest", Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(lookups + 1, f.Robust.Lookups);
    }

    [Fact]
    public async Task Suspension_on_people_page_ends_guest_session_at_once()
    {
        await using var f = Factory();
        var guest = Client(f);
        using (await SignInAsync(guest, "Test Guest", "pass-word1")) { }
        using (var before = await guest.GetAsync("/guest", Ct))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var supervisor = Client(f);
        (await supervisor.GetAsync("/__test/signin?role=supervisor", Ct)).Dispose();
        var people = new HtmlParser().ParseDocument(await supervisor.GetStringAsync("/people", Ct));
        var token = people.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var suspend = await supervisor.PostAsync("/people?handler=Suspend", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Test Guest",
            ["__RequestVerificationToken"] = token,
        }), Ct);

        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        Assert.Equal(-1, f.Robust.All.Single(a => a.PrincipalId == TestAuth.GuestId).UserLevel);
        // No time has passed: the guest's next request, not the five-minute recheck, ends the session.
        using var after = await guest.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Equal("/guest/sign-in", LocationPath(after));
        Assert.NotEqual("0", f.Services.GetRequiredService<GuestStamps>().StampOf(TestAuth.GuestId));
    }
}
