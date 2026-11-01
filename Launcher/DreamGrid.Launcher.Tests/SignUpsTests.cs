using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The sign-up service in the real host, with a fake Robust and a fake mail transport (spec 2026-10-31-sign-up-design §4, §5). All names are made up.</summary>
public sealed partial class SignUpsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IPAddress Caller = IPAddress.Parse("192.168.1.60");
    private static readonly DateTimeOffset Saved = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private const string Password = "tall-green-kettle";

    [GeneratedRegex(@"token=([A-Za-z0-9_\-]+)")]
    private static partial Regex TokenIn();

    internal sealed record Rig(LauncherFactory F, EmailPageTests.FakeTransport Transport) : IAsyncDisposable
    {
        public SignUps SignUps => F.Services.GetRequiredService<SignUps>();
        public SignUpBoard Pending => F.Services.GetRequiredService<SignUpBoard>();
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    internal static LegalSettings WrittenLegal() => new(
        new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
        {
            ["en"] = new[] { "terms", "privacy", "cookies" }.ToDictionary(p => p, p => new LegalText($"Test {p}.", 4, Saved)),
        },
        true,
        Saved);

    internal static Rig Factory(bool open = true, bool email = true, Action<LauncherFactory>? before = null)
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Legal = WrittenLegal(),
            SignUp = new SignUpSettings(Open: open),
            Bots = new BotSettings(Honeypot: true, RateLimit: false, ProofOfWork: false),
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, "old-password-1", email: "ann@example.com");
        if (email)
        {
            var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
                "grid@gmail.com", null, 500, null, false, 0, null, null);
            f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        }
        before?.Invoke(f);
        return new Rig(f, transport);
    }

    internal static SignUpForm Form(string first = "Bea", string last = "Example", string email = "bea@example.com",
        string password = Password, string? again = null, bool terms = true) => new(first, last, email, password, again ?? password, terms);

    internal static async Task<List<(EmailAccount Account, string Password, string To, RenderedEmail Email)>> SentAsync(Rig rig, int atLeast)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (rig.Transport.Sent)
                if (rig.Transport.Sent.Count >= atLeast) return [.. rig.Transport.Sent];
            await Task.Delay(25, Ct);
        }
        lock (rig.Transport.Sent) return [.. rig.Transport.Sent];
    }

    internal static string LinkToken(RenderedEmail email) => TokenIn().Match(email.Text).Groups[1].Value;

    private static string AuditText(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    private static List<string> Actions(LauncherFactory f) => AuditText(f).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString()!).ToList();

    [Fact]
    public async Task Closed_makes_nothing()
    {
        await using var rig = Factory(open: false);

        var r = await rig.SignUps.SubmitAsync(Form(), Caller, Ct);

        Assert.Equal(SignUpOutcome.Closed, r.Outcome);
        Assert.DoesNotContain(rig.F.Robust.All, a => a.FirstName == "Bea");
    }

    [Fact]
    public async Task Problems_in_order()
    {
        await using var rig = Factory();

        var r = await rig.SignUps.SubmitAsync(Form(first: "Be a", password: "short", email: "nope", terms: false), Caller, Ct);

        Assert.Equal(SignUpOutcome.Invalid, r.Outcome);
        Assert.Equal(SignUpRules.NameProblem("Be a", "Example", rig.F.Store.LoadOrCreate()), r.Problem);
        r = await rig.SignUps.SubmitAsync(Form(password: "short", email: "nope", terms: false), Caller, Ct);
        Assert.Equal("Use at least 8 characters.", r.Problem);
        r = await rig.SignUps.SubmitAsync(Form(email: "nope", terms: false), Caller, Ct);
        Assert.Equal(SignUpRules.EmailProblem("nope"), r.Problem);
        r = await rig.SignUps.SubmitAsync(Form(terms: false), Caller, Ct);
        Assert.Equal("Tick the box to accept the Terms of Service and the Privacy notice.", r.Problem);
        Assert.DoesNotContain(rig.F.Robust.All, a => a.FirstName == "Bea");
    }

    [Fact]
    public async Task Name_taken_said()
    {
        await using var rig = Factory();

        var r = await rig.SignUps.SubmitAsync(Form(first: "ann", last: "smith", email: "other@example.com"), Caller, Ct);

        Assert.Equal(SignUpOutcome.NameTaken, r.Outcome);
        Assert.Equal("That name is taken. Try another.", r.Problem);
    }

    [Fact]
    public async Task Email_taken_not_said_and_notice_sent()
    {
        await using var rig = Factory();

        var r = await rig.SignUps.SubmitAsync(Form(email: "ann@example.com"), Caller, Ct);

        Assert.Equal(SignUpOutcome.CheckEmail, r.Outcome);
        await Task.Delay(200, Ct);
        var sent = Assert.Single(await SentAsync(rig, 1));
        Assert.Equal("ann@example.com", sent.To);
        Assert.Contains("Ann Smith", sent.Email.Text);
        Assert.Contains("/guest/forgot-password", sent.Email.Text);
    }

    [Fact]
    public async Task Email_taken_ignores_case_and_spaces()
    {
        await using var rig = Factory();

        var r = await rig.SignUps.SubmitAsync(Form(email: "  ANN@Example.com "), Caller, Ct);

        Assert.Equal(SignUpOutcome.CheckEmail, r.Outcome);
        await Task.Delay(200, Ct);
        Assert.Equal("Someone tried to sign up on Avalon", Assert.Single(await SentAsync(rig, 1)).Email.Subject);
    }

    [Fact]
    public async Task Email_taken_holds_the_name_like_any_sign_up()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(email: "ann@example.com"), Caller, Ct);

        // Had the address been free, the name would be held now; it must be held either way.
        var again = await rig.SignUps.SubmitAsync(Form(email: "fresh@example.com"), Caller, Ct);

        Assert.Equal(SignUpOutcome.NameTaken, again.Outcome);
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea");
        Assert.Equal(-1, bea.UserLevel);
        Assert.True(rig.Pending.Contains(bea.PrincipalId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_delete_is_left_for_the_clean_up(bool email)
    {
        await using var rig = Factory(email: email);
        rig.F.Robust.PasswordsDown = true;
        rig.F.Remover.Down = true;

        Assert.Equal(SignUpOutcome.Busy, (await rig.SignUps.SubmitAsync(Form(), Caller, Ct)).Outcome);

        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea");
        Assert.Equal(-1, bea.UserLevel);
        Assert.True(rig.Pending.Contains(bea.PrincipalId));
    }

    [Fact]
    public async Task Unreadable_pending_list_is_busy_and_keeps_the_link()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(), Caller, Ct);
        var token = LinkToken(Assert.Single(await SentAsync(rig, 1)).Email);
        var file = Path.Combine(rig.F.Paths.Guests, "pending-signups.json");
        var saved = File.ReadAllText(file);
        File.Delete(file);
        Directory.CreateDirectory(file);

        await Assert.ThrowsAsync<SignUpBusyException>(() => rig.SignUps.ConfirmAsync(token, Caller, Ct));

        Directory.Delete(file);
        File.WriteAllText(file, saved);
        Assert.NotNull(await rig.SignUps.ConfirmAsync(token, Caller, Ct));
    }

    [Fact]
    public async Task With_email_avatar_is_pending()
    {
        await using var rig = Factory();

        var r = await rig.SignUps.SubmitAsync(Form(), Caller, Ct);

        Assert.Equal(SignUpOutcome.CheckEmail, r.Outcome);
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea");
        Assert.Equal(-1, bea.UserLevel);
        Assert.True(rig.Pending.Contains(bea.PrincipalId));
        Assert.True(await rig.F.Robust.AuthenticateAsync(bea.PrincipalId, Password, Ct));
        var sent = Assert.Single(await SentAsync(rig, 1));
        Assert.Equal("bea@example.com", sent.To);
        Assert.Contains("/guest/confirm?token=", sent.Email.Text);
        Assert.Contains("24 hours", sent.Email.Text);
    }

    [Fact]
    public async Task Without_email_avatar_is_level_zero_and_not_pending()
    {
        await using var rig = Factory(email: false);

        var r = await rig.SignUps.SubmitAsync(Form(email: "ann@example.com"), Caller, Ct);

        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.NotNull(r.Account);
        Assert.Equal(0, r.Account.UserLevel);
        Assert.False(rig.Pending.Contains(r.Account.PrincipalId));
        Assert.True(await rig.F.Robust.AuthenticateAsync(r.Account.PrincipalId, Password, Ct));
    }

    [Fact]
    public async Task Terms_recorded_at_sign_up()
    {
        await using var rig = Factory(email: false);

        var r = await rig.SignUps.SubmitAsync(Form(), Caller, Ct);

        Assert.True(rig.F.Services.GetRequiredService<TermsAcceptances>().HasAccepted(r.Account!.PrincipalId, 4));
    }

    [Fact]
    public async Task Password_step_failure_deletes_the_avatar()
    {
        await using var rig = Factory();
        rig.F.Robust.PasswordsDown = true;

        var r = await rig.SignUps.SubmitAsync(Form(), Caller, Ct);

        Assert.Equal(SignUpOutcome.Busy, r.Outcome);
        Assert.DoesNotContain(rig.F.Robust.All, a => a.FirstName == "Bea");
        Assert.Single(rig.F.Remover.Deleted);
        Assert.Empty(await SentAsync(rig, 0));
    }

    [Fact]
    public async Task Robust_down_is_busy()
    {
        await using var rig = Factory();
        rig.F.Grid.OnCreateResident = (_, _, _, _) => throw new HttpRequestException("Connection refused");

        Assert.Equal(SignUpOutcome.Busy, (await rig.SignUps.SubmitAsync(Form(), Caller, Ct)).Outcome);
    }

    [Fact]
    public async Task Confirm_raises_level_and_spends_link()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(), Caller, Ct);
        var token = LinkToken(Assert.Single(await SentAsync(rig, 1)).Email);

        var account = await rig.SignUps.ConfirmAsync(token, Caller, Ct);

        Assert.NotNull(account);
        Assert.Equal(0, account.UserLevel);
        Assert.Equal(0, rig.F.Robust.All.Single(a => a.FirstName == "Bea").UserLevel);
        Assert.False(rig.Pending.Contains(account.PrincipalId));
        Assert.Null(await rig.SignUps.ConfirmAsync(token, Caller, Ct));
    }

    [Fact]
    public async Task Confirm_failure_keeps_link()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(), Caller, Ct);
        var token = LinkToken(Assert.Single(await SentAsync(rig, 1)).Email);
        rig.F.Grid.SetUserLevelError = new HttpRequestException("Connection refused");

        await Assert.ThrowsAsync<SignUpBusyException>(() => rig.SignUps.ConfirmAsync(token, Caller, Ct));

        rig.F.Grid.SetUserLevelError = null;
        Assert.NotNull(await rig.SignUps.ConfirmAsync(token, Caller, Ct));
    }

    [Fact]
    public async Task Confirm_refuses_unlisted_principal()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(), Caller, Ct);
        var token = LinkToken(Assert.Single(await SentAsync(rig, 1)).Email);
        var bea = rig.F.Robust.All.Single(a => a.FirstName == "Bea");
        rig.Pending.Remove(bea.PrincipalId);

        Assert.Null(await rig.SignUps.ConfirmAsync(token, Caller, Ct));
        Assert.Equal(-1, rig.F.Robust.All.Single(a => a.FirstName == "Bea").UserLevel);
    }

    [Fact]
    public async Task Resend_replaces_link_and_counts()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(), Caller, Ct);
        var first = LinkToken(Assert.Single(await SentAsync(rig, 1)).Email);

        Assert.Equal(SignUpOutcome.CheckEmail, await rig.SignUps.ResendAsync("Bea", "Example", Caller, Ct));

        var sent = await SentAsync(rig, 2);
        Assert.Equal(2, sent.Count);
        Assert.Null(await rig.SignUps.ConfirmAsync(first, Caller, Ct));
        Assert.NotNull(await rig.SignUps.ConfirmAsync(LinkToken(sent[1].Email), Caller, Ct));
    }

    [Fact]
    public async Task Resend_for_a_confirmed_or_unknown_name_sends_nothing()
    {
        await using var rig = Factory();

        Assert.Equal(SignUpOutcome.CheckEmail, await rig.SignUps.ResendAsync("Ann", "Smith", Caller, Ct));
        Assert.Equal(SignUpOutcome.CheckEmail, await rig.SignUps.ResendAsync("No", "Body", Caller, Ct));
        Assert.Empty(await SentAsync(rig, 1));
    }

    [Fact]
    public async Task Resend_without_email_is_busy()
    {
        await using var rig = Factory(email: false);

        Assert.Equal(SignUpOutcome.Busy, await rig.SignUps.ResendAsync("Bea", "Example", Caller, Ct));
    }

    [Fact]
    public async Task Fourth_email_in_an_hour_not_sent()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(), Caller, Ct);
        await rig.SignUps.ResendAsync("Bea", "Example", Caller, Ct);
        await rig.SignUps.ResendAsync("Bea", "Example", Caller, Ct);
        Assert.Equal(3, (await SentAsync(rig, 3)).Count);

        Assert.Equal(SignUpOutcome.CheckEmail, await rig.SignUps.ResendAsync("Bea", "Example", Caller, Ct));

        await Task.Delay(200, Ct);
        Assert.Equal(3, (await SentAsync(rig, 4)).Count);
    }

    [Fact]
    public async Task Nothing_secret_audited()
    {
        await using var rig = Factory();
        await rig.SignUps.SubmitAsync(Form(), Caller, Ct);
        var token = LinkToken(Assert.Single(await SentAsync(rig, 1)).Email);
        await rig.SignUps.ConfirmAsync(token, Caller, Ct);
        await rig.SignUps.SubmitAsync(Form(first: "Cy", email: "ann@example.com"), Caller, Ct);

        var text = AuditText(rig.F);
        Assert.Contains("signup-made", Actions(rig.F));
        Assert.Contains("signup-confirmed", Actions(rig.F));
        Assert.Contains("signup-refused", Actions(rig.F));
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain("@example.com", text);
    }

    [Fact]
    public void Already_have_account_template_in_All()
    {
        Assert.Contains(EmailTemplates.AlreadyHaveAccount, EmailTemplates.All);
        Assert.Equal(["{Link}"], EmailTemplates.Required(EmailTemplates.AlreadyHaveAccount));
        Assert.Equal("Someone tried to sign up on {GridName}", EmailTemplates.Default(EmailTemplates.AlreadyHaveAccount).Subject);
    }
}
