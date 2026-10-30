using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Two-factor sign-in on the real host: the code step, the required switch, and the two-factor page.</summary>
public sealed class TwoFactorSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Password = "pass-word1";
    private const string WrongCodeMessage = "That code isn't right. Try again.";
    private const string Locked = "Too many attempts. Try again in a few minutes.";
    private const string Unknown =
        "Two-factor sign-in can't be checked on this computer. The grid owner can run reset-two-factor on the grid computer.";
    private const string Required = "The grid owner requires two-factor sign-in.";
    private const string SaveFailed = "The two-factor file couldn't be saved. Try again.";

    private static LauncherFactory Factory(bool required = false)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with
            {
                WorldNamed = true, OwnerCreated = true, RegionCreated = true, Finished = true,
                Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId),
            },
            Security = s.Security with { RequireTwoFactorForStaff = required },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static string? LocationPath(HttpResponseMessage r) =>
        r.Headers.Location is { IsAbsoluteUri: true } u ? u.AbsolutePath : r.Headers.Location?.OriginalString.Split('?')[0];

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

    private static async Task<HttpResponseMessage> PasswordAsync(HttpClient client, string name, string password = Password)
    {
        var page = await PageAsync(client, "/login");
        return await PostAsync(client, "/login",
            new() { ["Name"] = name, ["Password"] = password, ["__RequestVerificationToken"] = Token(page) });
    }

    private static async Task<HttpResponseMessage> CodeAsync(HttpClient client, string code)
    {
        var page = await PageAsync(client, "/login/code");
        return await PostAsync(client, "/login/code", new() { ["Code"] = code, ["__RequestVerificationToken"] = Token(page) });
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector(".error")?.TextContent.Trim();

    private static async Task<HttpStatusCode> DashboardAsync(HttpClient client)
    {
        using var r = await client.GetAsync("/dashboard", Ct);
        return r.StatusCode;
    }

    /// <summary>Turns two-factor on for <paramref name="role"/>'s account through the store, then moves the clock to the next
    /// step so the confirming code's step is not the current one.</summary>
    private static (byte[] Secret, IReadOnlyList<string> Backup) Enrol(LauncherFactory f, StaffRole role)
    {
        var store = f.Services.GetRequiredService<TwoFactorStore>();
        var account = TestAuth.Account(role);
        var (secret, _) = store.BeginEnrol(account.PrincipalId, $"{account.FirstName} {account.LastName}", "Avalon");
        var backup = store.ConfirmEnrol(account.PrincipalId, CodeNow(f, secret), secret);
        Assert.NotNull(backup);
        f.Time.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
        return (secret, backup);
    }

    private static string CodeNow(LauncherFactory f, byte[] secret) => Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow()));

    /// <summary>Six digits that are none of the codes accepted now.</summary>
    private static string WrongCode(LauncherFactory f, byte[] secret)
    {
        var step = Totp.StepAt(f.Time.GetUtcNow());
        var valid = new[] { Totp.Code(secret, step - 1), Totp.Code(secret, step), Totp.Code(secret, step + 1) };
        return new[] { "000000", "111111", "222222", "333333" }.First(c => !valid.Contains(c));
    }

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static int AuditCount(LauncherFactory f, string action) =>
        Audit(f).Count(e => e.GetProperty("action").GetString() == action);

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

    [Fact]
    public async Task Enrolled_admin_needs_code()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var client = Client(f);

        using var password = await PasswordAsync(client, "Test Admin");
        Assert.Equal(HttpStatusCode.Redirect, password.StatusCode);
        Assert.Equal("/login/code", LocationPath(password));
        // The pending cookie is only for the code step, and the password alone signs nobody in.
        var pending = Assert.Single(password.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-2fa=", StringComparison.Ordinal));
        Assert.Contains("path=/login", pending, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", pending, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", pending, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));
        Assert.Equal(0, AuditCount(f, "sign-in"));

        using var code = await CodeAsync(client, CodeNow(f, secret));

        Assert.Equal(HttpStatusCode.Redirect, code.StatusCode);
        Assert.Equal("/", LocationPath(code));
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(client));
        var entry = Assert.Single(Audit(f), e => e.GetProperty("action").GetString() == "sign-in");
        Assert.Equal("Test Admin", entry.GetProperty("actor").GetString());
        Assert.Equal(0, AuditCount(f, "backup-code-used"));
        // The pending cookie is spent: the code page sends back to the password.
        using var again = await client.GetAsync("/login/code", Ct);
        Assert.Equal("/login", LocationPath(again));
    }

    [Fact]
    public async Task Code_page_without_password_step_goes_to_sign_in()
    {
        await using var f = Factory();
        var client = Client(f);
        using var r = await client.GetAsync("/login/code", Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", LocationPath(r));
    }

    [Fact]
    public async Task Pending_code_step_expires_after_five_minutes()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var client = Client(f);
        (await PasswordAsync(client, "Test Admin")).Dispose();
        var page = await PageAsync(client, "/login/code");

        f.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using var r = await PostAsync(client, "/login/code",
            new() { ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = Token(page) });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", LocationPath(r));
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));
    }

    [Fact]
    public async Task Wrong_code_counts_toward_lock()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var client = Client(f);
        using (var r = await PasswordAsync(client, "Test Admin"))
            Assert.Equal("/login/code", LocationPath(r));

        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
        {
            using var wrong = await CodeAsync(client, WrongCode(f, secret));
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Equal(WrongCodeMessage, await ErrorAsync(wrong));
        }

        using var right = await CodeAsync(client, CodeNow(f, secret));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(Locked, await ErrorAsync(right));
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));
        // The password step is locked for that name too.
        using var password = await PasswordAsync(Client(f), "Test Admin");
        Assert.Equal(Locked, await ErrorAsync(password));
    }

    [Fact]
    public async Task Backup_code_signs_in_once()
    {
        await using var f = Factory();
        var (_, backup) = Enrol(f, StaffRole.Admin);

        var first = Client(f);
        (await PasswordAsync(first, "Test Admin")).Dispose();
        using (var r = await CodeAsync(first, backup[2]))
            Assert.Equal("/", LocationPath(r));
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(first));
        Assert.Equal(1, AuditCount(f, "backup-code-used"));
        Assert.Equal(1, AuditCount(f, "sign-in"));
        Assert.DoesNotContain(Audit(f), e => e.ToString().Contains(backup[2], StringComparison.OrdinalIgnoreCase));

        var second = Client(f);
        (await PasswordAsync(second, "Test Admin")).Dispose();
        using (var r = await CodeAsync(second, backup[2]))
            Assert.Equal(WrongCodeMessage, await ErrorAsync(r));
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(second));
        Assert.Equal(1, AuditCount(f, "sign-in"));
    }

    [Fact]
    public async Task Required_switch_sends_admin_to_enrol()
    {
        await using var f = Factory(required: true);
        var client = Client(f);

        using (var r = await PasswordAsync(client, "Test Admin"))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/profile/two-factor", LocationPath(r));
        }
        using (var r = await client.GetAsync("/dashboard", Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/profile/two-factor", LocationPath(r));
        }
        using (var r = await client.GetAsync("/profile/sessions", Ct))
            Assert.Equal("/profile/two-factor", LocationPath(r));
        using (var r = await client.GetAsync("/api/status", Ct))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await client.GetAsync("/lib/bootstrap/bootstrap.min.css", Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var page = await PageAsync(client, "/profile/two-factor");
        Assert.Contains(Required, page.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.Null(page.QuerySelector("nav a[href='/regions']")); // only the two-factor page and sign out
        var svg = page.QuerySelector(".two-factor-qr svg");
        Assert.NotNull(svg);
        Assert.Null(page.QuerySelector("[style]"));
        var secret = FromBase32(page.QuerySelector("#two-factor-secret")!.TextContent);
        Assert.Equal(20, secret.Length);
        var form = page.QuerySelector("form#turn-on")!;
        using var wrong = await PostAsync(client, "/profile/two-factor?handler=TurnOn", new()
        {
            ["Code"] = WrongCode(f, secret),
            ["Shown"] = form.QuerySelector("input[name=Shown]")!.GetAttribute("value")!,
            ["__RequestVerificationToken"] = Token(page, "form#turn-on"),
        });
        Assert.Equal(WrongCodeMessage, await ErrorAsync(wrong));
        // The same secret is shown again, so the app's entry still works.
        var retry = new HtmlParser().ParseDocument(await wrong.Content.ReadAsStringAsync(Ct));
        Assert.Equal(secret, FromBase32(retry.QuerySelector("#two-factor-secret")!.TextContent));
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));

        using var on = await PostAsync(client, "/profile/two-factor?handler=TurnOn", new()
        {
            ["Code"] = CodeNow(f, secret),
            ["Shown"] = retry.QuerySelector("form#turn-on input[name=Shown]")!.GetAttribute("value")!,
            ["__RequestVerificationToken"] = Token(retry, "form#turn-on"),
        });

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var shown = new HtmlParser().ParseDocument(await on.Content.ReadAsStringAsync(Ct));
        var codes = shown.QuerySelectorAll("#backup-codes li").Select(li => li.TextContent.Trim()).ToList();
        Assert.Equal(BackupCodes.Count, codes.Count);
        Assert.Equal(TwoFactorState.On, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.AdminId));
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(client));
        Assert.Equal(1, AuditCount(f, "two-factor-on"));
        // The old session (the one that had to enrol) is over; this browser has a new one.
        Assert.Single(f.Services.GetRequiredService<StaffSessions>().List(TestAuth.AdminId));
        Assert.DoesNotContain(f.Logs, l => codes.Any(c => l.Message.Contains(c, StringComparison.OrdinalIgnoreCase)));
        // The page was rendered for the new cookie: its links are the ordinary ones and its forms' tokens are good.
        Assert.NotNull(shown.QuerySelector("nav a[href='/regions']"));
        using var signOut = await PostAsync(client, "/logout",
            new() { ["__RequestVerificationToken"] = Token(shown, "form[action='/logout']") });
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.Equal("/login", LocationPath(signOut));
    }

    [Fact]
    public async Task Required_switch_sends_supervisor_to_enrol()
    {
        await using var f = Factory(required: true);
        var client = Client(f);
        using (var r = await PasswordAsync(client, "Test Supervisor"))
            Assert.Equal("/profile/two-factor", LocationPath(r));
        using (var r = await client.GetAsync("/profile/sessions", Ct))
            Assert.Equal("/profile/two-factor", LocationPath(r));
    }

    [Fact]
    public async Task Required_switch_does_not_apply_to_owner()
    {
        await using var f = Factory(required: true);
        var client = Client(f);
        using (var r = await PasswordAsync(client, "Test Owner"))
            Assert.Equal("/", LocationPath(r));
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(client));
    }

    [Fact]
    public async Task Unknown_state_refuses_staff_sign_in()
    {
        await using var f = Factory();
        Directory.CreateDirectory(f.Paths.Security);
        File.WriteAllText(Path.Combine(f.Paths.Security, "twofactor.json"), "{ this is not json");
        var client = Client(f);

        foreach (var name in new[] { "Test Admin", "Test Owner" })
        {
            using var r = await PasswordAsync(client, name);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(Unknown, await ErrorAsync(r));
            Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));
        }
        Assert.Contains(f.Logs, l => l.Level == LogLevel.Warning && l.Category == typeof(Pages.LoginModel).FullName
            && l.Message.Contains("two-factor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, AuditCount(f, "sign-in"));
    }

    [Fact]
    public async Task Admin_cannot_turn_off_while_required()
    {
        await using var f = Factory(required: true);
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var client = Client(f);
        (await client.GetAsync("/__test/signin?role=admin", Ct)).Dispose();

        var page = await PageAsync(client, "/profile/two-factor");
        Assert.Contains(Required, page.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.Null(page.QuerySelector("form#turn-off"));
        using var r = await PostAsync(client, "/profile/two-factor?handler=TurnOff",
            new() { ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = Token(page) });

        Assert.Equal(Required, await ErrorAsync(r));
        Assert.Equal(TwoFactorState.On, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.AdminId));
        Assert.Equal(0, AuditCount(f, "two-factor-off"));
    }

    [Fact]
    public async Task Owner_can_turn_off_while_required()
    {
        await using var f = Factory(required: true);
        var (secret, _) = Enrol(f, StaffRole.Owner);
        var client = Client(f);
        (await client.GetAsync("/__test/signin?role=owner", Ct)).Dispose();

        var page = await PageAsync(client, "/profile/two-factor");
        using var wrong = await PostAsync(client, "/profile/two-factor?handler=TurnOff",
            new() { ["Code"] = WrongCode(f, secret), ["__RequestVerificationToken"] = Token(page, "form#turn-off") });
        Assert.Equal(WrongCodeMessage, await ErrorAsync(wrong));

        using var r = await PostAsync(client, "/profile/two-factor?handler=TurnOff",
            new() { ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = Token(page, "form#turn-off") });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(TwoFactorState.Off, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.OwnerId));
        Assert.Equal(1, AuditCount(f, "two-factor-off"));
    }

    [Fact]
    public async Task A_right_password_on_port_8001_does_not_reset_the_count_of_wrong_codes()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var client = Client(f);
        using (var r = await PasswordAsync(client, "Test Admin"))
            Assert.Equal("/login/code", LocationPath(r));
        for (var i = 0; i < LoginThrottle.NameMaxFailures - 1; i++)
        {
            using var wrong = await CodeAsync(client, WrongCode(f, secret));
            Assert.Equal(WrongCodeMessage, await ErrorAsync(wrong));
        }

        // The password is right, but two-factor is on: refused, and the count of wrong codes stands.
        using (var command = await Client(f).GetAsync($"/?command=StopRobust&avatar=Test%20Admin&password={Password}", Ct))
            Assert.StartsWith("NAK", await command.Content.ReadAsStringAsync(Ct));
        using (var fifth = await CodeAsync(client, WrongCode(f, secret)))
            Assert.Equal(WrongCodeMessage, await ErrorAsync(fifth));

        using var right = await CodeAsync(client, CodeNow(f, secret));
        Assert.Equal(Locked, await ErrorAsync(right));
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));
    }

    [Fact]
    public async Task Wrong_codes_on_the_two_factor_page_count_toward_lock()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Owner);
        var client = Client(f);
        (await client.GetAsync("/__test/signin?role=owner", Ct)).Dispose();
        var page = await PageAsync(client, "/profile/two-factor");
        var token = Token(page, "form#turn-off");

        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
        {
            using var wrong = await PostAsync(client, "/profile/two-factor?handler=" + (i % 2 == 0 ? "TurnOff" : "NewCodes"),
                new() { ["Code"] = WrongCode(f, secret), ["__RequestVerificationToken"] = token });
            Assert.Equal(WrongCodeMessage, await ErrorAsync(wrong));
        }
        using var r = await PostAsync(client, "/profile/two-factor?handler=TurnOff",
            new() { ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = token });

        Assert.Equal(Locked, await ErrorAsync(r));
        Assert.Equal(TwoFactorState.On, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.OwnerId));
        var refused = Audit(f).Where(e => e.GetProperty("action").GetString() == "sign-in-refused").ToList();
        Assert.Equal(LoginThrottle.NameMaxFailures, refused.Count);
        Assert.All(refused, e => Assert.Equal("Test Owner", e.GetProperty("actor").GetString()));
    }

    [Fact]
    public async Task New_backup_codes_replace_the_old_ones()
    {
        await using var f = Factory();
        var (secret, old) = Enrol(f, StaffRole.Admin);
        var client = Client(f);
        (await client.GetAsync("/__test/signin?role=admin", Ct)).Dispose();

        var page = await PageAsync(client, "/profile/two-factor");
        using var r = await PostAsync(client, "/profile/two-factor?handler=NewCodes",
            new() { ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = Token(page, "form#new-codes") });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var shown = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        var codes = shown.QuerySelectorAll("#backup-codes li").Select(li => li.TextContent.Trim()).ToList();
        Assert.Equal(BackupCodes.Count, codes.Count);
        var store = f.Services.GetRequiredService<TwoFactorStore>();
        Assert.False(store.VerifyCode(TestAuth.AdminId, old[0], out _));
        Assert.True(store.VerifyCode(TestAuth.AdminId, codes[0], out _));
        Assert.Equal(1, AuditCount(f, "backup-codes-new"));
    }

    [Fact]
    public async Task Code_that_signed_in_is_refused_in_another_browser_in_the_same_step()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var code = CodeNow(f, secret);

        var first = Client(f);
        (await PasswordAsync(first, "Test Admin")).Dispose();
        using (var r = await CodeAsync(first, code))
            Assert.Equal("/", LocationPath(r));

        var second = Client(f);
        (await PasswordAsync(second, "Test Admin")).Dispose();
        using (var r = await CodeAsync(second, code))
            Assert.Equal(WrongCodeMessage, await ErrorAsync(r));
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(second));
    }

    [Fact]
    public async Task Turn_off_with_a_backup_code()
    {
        await using var f = Factory();
        var (_, backup) = Enrol(f, StaffRole.Owner);
        var client = Client(f);
        (await client.GetAsync("/__test/signin?role=owner", Ct)).Dispose();
        var page = await PageAsync(client, "/profile/two-factor");

        using var r = await PostAsync(client, "/profile/two-factor?handler=TurnOff",
            new() { ["Code"] = backup[0], ["__RequestVerificationToken"] = Token(page, "form#turn-off") });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(TwoFactorState.Off, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.OwnerId));
        Assert.Equal(1, AuditCount(f, "backup-code-used"));
        Assert.Equal(1, AuditCount(f, "two-factor-off"));
    }

    [Fact]
    public async Task Pending_sign_in_is_refused_once_two_factor_was_reset()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var client = Client(f);
        (await PasswordAsync(client, "Test Admin")).Dispose();
        var page = await PageAsync(client, "/login/code");

        f.Services.GetRequiredService<TwoFactorStore>().Disable(TestAuth.AdminId);
        using var r = await PostAsync(client, "/login/code",
            new() { ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = Token(page) });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", LocationPath(r));
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));
        Assert.Equal(0, AuditCount(f, "sign-in"));
    }

    [Fact]
    public async Task Code_that_cannot_be_saved_is_not_a_wrong_code()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Admin);
        var client = Client(f);
        (await PasswordAsync(client, "Test Admin")).Dispose();
        var blocker = Path.Combine(f.Paths.Security, "twofactor.json.tmp");
        Directory.CreateDirectory(blocker); // the save's temp file can't be written

        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
        {
            using var r = await CodeAsync(client, CodeNow(f, secret));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(SaveFailed, await ErrorAsync(r));
        }
        Assert.Equal(HttpStatusCode.Redirect, await DashboardAsync(client));

        // Not counted by the throttle, and the code wasn't used up: it works once the file can be saved.
        Directory.Delete(blocker);
        using (var r = await CodeAsync(client, CodeNow(f, secret)))
            Assert.Equal("/", LocationPath(r));
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(client));
        Assert.Equal(0, AuditCount(f, "sign-in-refused"));
    }

    [Fact]
    public async Task Turn_off_that_cannot_be_saved_says_so()
    {
        await using var f = Factory();
        var (secret, _) = Enrol(f, StaffRole.Owner);
        var client = Client(f);
        (await client.GetAsync("/__test/signin?role=owner", Ct)).Dispose();
        var page = await PageAsync(client, "/profile/two-factor");
        Directory.CreateDirectory(Path.Combine(f.Paths.Security, "twofactor.json.tmp"));

        for (var i = 0; i < LoginThrottle.NameMaxFailures; i++)
        {
            using var r = await PostAsync(client, "/profile/two-factor?handler=" + (i % 2 == 0 ? "TurnOff" : "NewCodes"),
                new() { ["Code"] = CodeNow(f, secret), ["__RequestVerificationToken"] = Token(page, "form#turn-off") });
            Assert.Equal(SaveFailed, await ErrorAsync(r));
        }

        Assert.Equal(TwoFactorState.On, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.OwnerId));
        Assert.Equal(0, AuditCount(f, "sign-in-refused"));
        Assert.False(f.Services.GetRequiredService<LoginThrottle>().IsLocked("Test Owner", null));
    }

    [Fact]
    public async Task Enrolment_form_survives_the_five_minute_recheck()
    {
        await using var f = Factory(required: true);
        var client = Client(f);
        (await PasswordAsync(client, "Test Admin")).Dispose();
        var page = await PageAsync(client, "/profile/two-factor");
        var secret = FromBase32(page.QuerySelector("#two-factor-secret")!.TextContent);

        f.Time.Advance(StaffPrincipalValidator.RecheckEvery + TimeSpan.FromSeconds(1));
        using var on = await PostAsync(client, "/profile/two-factor?handler=TurnOn", new()
        {
            ["Code"] = CodeNow(f, secret),
            ["Shown"] = page.QuerySelector("form#turn-on input[name=Shown]")!.GetAttribute("value")!,
            ["__RequestVerificationToken"] = Token(page, "form#turn-on"),
        });

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var shown = new HtmlParser().ParseDocument(await on.Content.ReadAsStringAsync(Ct));
        Assert.Equal(BackupCodes.Count, shown.QuerySelectorAll("#backup-codes li").Length);
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(client));
    }

    [Fact]
    public async Task Admin_form_survives_the_five_minute_recheck()
    {
        await using var f = Factory();
        var client = Client(f);
        (await client.GetAsync("/__test/signin?role=admin", Ct)).Dispose();
        var page = await PageAsync(client, "/dashboard");

        f.Time.Advance(StaffPrincipalValidator.RecheckEvery + TimeSpan.FromSeconds(1));
        using var r = await PostAsync(client, "/dashboard?handler=run",
            new() { ["__RequestVerificationToken"] = Token(page, "form#controls") });

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", LocationPath(r));
    }

    [Fact]
    public async Task Switch_turned_on_sends_signed_in_admin_to_enrol()
    {
        await using var f = Factory();
        var admin = Client(f);
        (await admin.GetAsync("/__test/signin?role=admin", Ct)).Dispose();
        var supervisor = Client(f);
        (await supervisor.GetAsync("/__test/signin?role=supervisor", Ct)).Dispose();
        var owner = Client(f);
        (await owner.GetAsync("/__test/signin?role=owner", Ct)).Dispose();
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(admin));

        var s = f.Store.LoadOrCreate();
        f.Store.Save(s with { Security = s.Security with { RequireTwoFactorForStaff = true } });

        using (var r = await admin.GetAsync("/dashboard", Ct))
            Assert.Equal("/profile/two-factor", LocationPath(r));
        using (var r = await admin.GetAsync("/api/status", Ct))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await supervisor.GetAsync("/profile/sessions", Ct))
            Assert.Equal("/profile/two-factor", LocationPath(r));
        var page = await PageAsync(admin, "/profile/two-factor");
        Assert.Contains(Required, page.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.Null(page.QuerySelector("nav a[href='/regions']"));
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(owner));

        // Once the admin has two-factor, the same cookie works again.
        Enrol(f, StaffRole.Admin);
        Assert.Equal(HttpStatusCode.OK, await DashboardAsync(admin));
    }

    [Fact]
    public void Qr_code_svg_has_no_style()
    {
        var svg = TwoFactorQr.Svg(Totp.OtpAuthUri("Avalon", "Test Admin", Totp.NewSecret()));
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("style", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewBox", svg, StringComparison.Ordinal);
    }
}
