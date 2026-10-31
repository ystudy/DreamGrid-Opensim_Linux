using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Two-factor for residents: its own file, its page shown only when the rule asks (or it is already on), and the guest door's code step.</summary>
public sealed class ResidentTwoFactorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory(RecoveryRule residents = RecoveryRule.EmailLinkAndCode)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Recovery = new RecoverySettings(residents, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLinkAndCode),
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static TwoFactorStore Residents(LauncherFactory f) => f.Services.GetRequiredService<ResidentTwoFactor>().Store;

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Token(IHtmlDocument page, string? form = null)
    {
        var token = page.QuerySelector((form ?? "") + " input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> form) =>
        await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);

    private static async Task<HttpResponseMessage> PasswordAsync(HttpClient client, string name, string password = "pass-word1")
    {
        var page = await PageAsync(client, "/guest/sign-in");
        return await PostAsync(client, "/guest/sign-in", new() { ["Name"] = name, ["Password"] = password, ["__RequestVerificationToken"] = Token(page) });
    }

    private static async Task<HttpResponseMessage> CodeAsync(HttpClient client, string code)
    {
        var page = await PageAsync(client, "/guest/sign-in/code");
        return await PostAsync(client, "/guest/sign-in/code", new() { ["Code"] = code, ["__RequestVerificationToken"] = Token(page) });
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector(".error")?.TextContent.Trim();

    private static (byte[] Secret, IReadOnlyList<string> Backup) Enrol(LauncherFactory f, TwoFactorStore store, Guid principal, string name)
    {
        var (secret, _) = store.BeginEnrol(principal, name, "Avalon");
        var backup = store.ConfirmEnrol(principal, CodeNow(f, secret), secret);
        Assert.NotNull(backup);
        f.Time.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
        return (secret, backup);
    }

    private static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.Trim().ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits < 8) continue;
            bytes.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
            buffer &= (1 << bits) - 1;
        }
        return [.. bytes];
    }


    private static string CodeNow(LauncherFactory f, byte[] secret) => Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow()));

    private static string WrongCode(LauncherFactory f, byte[] secret)
    {
        var step = Totp.StepAt(f.Time.GetUtcNow());
        var valid = new[] { Totp.Code(secret, step - 1), Totp.Code(secret, step), Totp.Code(secret, step + 1) };
        return Enumerable.Range(0, 1_000_000).Select(i => i.ToString("D6")).First(c => !valid.Contains(c));
    }

    [Fact]
    public async Task Resident_page_hidden_until_rule_asks()
    {
        await using (var f = Factory(RecoveryRule.EmailLink))
        {
            var client = Client(f);
            await client.GetAsync("/__test/guest-signin", Ct);
            using var r = await client.GetAsync("/guest/two-factor", Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.DoesNotContain("/guest/two-factor", await client.GetStringAsync("/guest", Ct));
        }
        await using (var f = Factory())
        {
            var client = Client(f);
            await client.GetAsync("/__test/guest-signin", Ct);
            var page = await PageAsync(client, "/guest/two-factor");
            Assert.NotNull(page.QuerySelector("form#turn-on"));
            Assert.Contains("/guest/two-factor", await client.GetStringAsync("/guest", Ct));
        }
    }

    [Fact]
    public async Task Resident_turns_two_factor_on_from_the_page()
    {
        await using var f = Factory();
        var client = Client(f);
        await client.GetAsync("/__test/guest-signin", Ct);
        var page = await PageAsync(client, "/guest/two-factor");
        var shown = page.QuerySelector("form#turn-on input[name=Shown]")!.GetAttribute("value")!;
        var secret = FromBase32(page.QuerySelector("#two-factor-secret")!.TextContent);

        using var r = await PostAsync(client, "/guest/two-factor?handler=TurnOn", new()
        {
            ["Shown"] = shown, ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = Token(page, "form#turn-on"),
        });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("backup-codes", await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal(TwoFactorState.On, Residents(f).StateOf(TestAuth.GuestId));
        Assert.Equal(TwoFactorState.Off, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.GuestId));
    }

    [Fact]
    public async Task Enrolled_resident_keeps_page_after_rule_relaxed()
    {
        await using var f = Factory(RecoveryRule.EmailLink);
        Enrol(f, Residents(f), TestAuth.GuestId, "Test Guest");
        var client = Client(f);
        await client.GetAsync("/__test/guest-signin", Ct);

        var page = await PageAsync(client, "/guest/two-factor");

        Assert.NotNull(page.QuerySelector("form#turn-off"));
    }

    [Fact]
    public async Task Guest_door_asks_code_of_enrolled_resident()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, Residents(f), TestAuth.GuestId, "Test Guest");
        var client = Client(f);

        using var password = await PasswordAsync(client, "Test Guest");
        Assert.Equal(HttpStatusCode.Redirect, password.StatusCode);
        Assert.Equal("/guest/sign-in/code", password.Headers.Location?.OriginalString);
        Assert.DoesNotContain(password.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-guest=", StringComparison.Ordinal));

        using var code = await CodeAsync(client, CodeNow(f, secret));
        Assert.Equal(HttpStatusCode.Redirect, code.StatusCode);
        Assert.Equal("/guest", code.Headers.Location?.OriginalString);
        using var home = await client.GetAsync("/guest", Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Guest_door_never_asks_staff_two_factor()
    {
        await using var f = Factory();
        var admin = TestAuth.Account(StaffRole.Admin);
        Enrol(f, f.Services.GetRequiredService<TwoFactorStore>(), admin.PrincipalId, "Test Admin");
        var client = Client(f);

        using var r = await PasswordAsync(client, $"{admin.FirstName} {admin.LastName}");

        Assert.Equal("/guest", r.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Wrong_guest_code_counts_in_guest_throttle()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, Residents(f), TestAuth.GuestId, "Test Guest");
        var client = Client(f);
        await PasswordAsync(client, "Test Guest");

        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
        {
            using var wrong = await CodeAsync(client, WrongCode(f, secret));
            Assert.Equal("That code isn't right. Try again.", await ErrorAsync(wrong));
        }
        using var locked = await CodeAsync(client, CodeNow(f, secret));
        Assert.Equal("Too many attempts. Try again in a few minutes.", await ErrorAsync(locked));

        var throttle = f.Services.GetRequiredService<LoginThrottle>();
        Assert.True(throttle.IsLocked(SignInDoor.Guest, AvatarName.Parse("Test Guest").Key, null));
        Assert.False(throttle.IsLocked(SignInDoor.Staff, AvatarName.Parse("Test Guest").Key, null));
    }

    [Fact]
    public async Task Backup_code_signs_in_once()
    {
        await using var f = Factory();
        var (_, backup) = Enrol(f, Residents(f), TestAuth.GuestId, "Test Guest");

        var first = Client(f);
        await PasswordAsync(first, "Test Guest");
        using var ok = await CodeAsync(first, backup[0]);
        Assert.Equal("/guest", ok.Headers.Location?.OriginalString);

        var second = Client(f);
        await PasswordAsync(second, "Test Guest");
        using var again = await CodeAsync(second, backup[0]);
        Assert.Equal("That code isn't right. Try again.", await ErrorAsync(again));
    }

    [Fact]
    public async Task Code_page_without_a_password_step_goes_back()
    {
        await using var f = Factory();
        using var r = await Client(f).GetAsync("/guest/sign-in/code", Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/sign-in", r.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Owner_resets_resident_two_factor_from_security_page()
    {
        await using var f = Factory();
        Enrol(f, Residents(f), TestAuth.GuestId, "Test Guest");
        var stampBefore = f.Services.GetRequiredService<GuestStamps>().StampOf(TestAuth.GuestId);
        var owner = Client(f);
        await owner.GetAsync("/__test/signin?role=owner", Ct);

        var page = await PageAsync(owner, "/security");
        var row = page.QuerySelector($"#resident-two-factor tr[data-person='{TestAuth.GuestId}']");
        Assert.NotNull(row);
        using var r = await PostAsync(owner, "/security?handler=ResetResident", new()
        {
            ["id"] = TestAuth.GuestId.ToString(), ["__RequestVerificationToken"] = Token(page, "#resident-two-factor"),
        });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(TwoFactorState.Off, Residents(f).StateOf(TestAuth.GuestId));
        Assert.NotEqual(stampBefore, f.Services.GetRequiredService<GuestStamps>().StampOf(TestAuth.GuestId));
    }
}
