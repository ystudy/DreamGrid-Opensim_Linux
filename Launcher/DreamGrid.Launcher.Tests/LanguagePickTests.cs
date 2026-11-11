using System.Globalization;
using System.Net;
using System.Security.Claims;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Resident;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Which language a resident page is shown in (LanguagePick) and that staff pages are left alone.</summary>
public sealed class LanguagePickTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Gail = Guid.Parse("0b000000-0000-0000-0000-00000000000b");

    private static ResidentLanguages Saved(Guid? who = null, string? code = null)
    {
        var r = new ResidentLanguages(Path.Combine(Path.GetTempPath(), $"dg-lang-{Guid.NewGuid():N}.json"));
        if (who is { } g && code is not null) r.Set(g, code);
        return r;
    }

    private static ClaimsPrincipal GuestPrincipal(Guid id) =>
        new(new ClaimsIdentity(
        [
            new Claim(Security.StaffSignIn.PrincipalClaim, id.ToString()),
            new Claim(ClaimTypes.Role, Security.AuthSchemes.GuestRole),
            new Claim(Security.StaffSignIn.StampClaim, "stamp"),
            new Claim(Security.StaffSignIn.CheckedClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
        ], "test"));

    private static string Pick(LanguageSettings? s = null, string? cookie = null, string? accept = null, string? query = null,
        ResidentLanguages? saved = null, ClaimsPrincipal? guest = null, bool welcome = false)
    {
        var ctx = new DefaultHttpContext();
        if (cookie is not null) ctx.Request.Headers.Cookie = $"{LanguagePick.Cookie}={cookie}";
        if (accept is not null) ctx.Request.Headers.AcceptLanguage = accept;
        if (query is not null) ctx.Request.QueryString = new QueryString($"?lang={query}");
        var none = new OwnerLanguages(Path.Combine(Path.GetTempPath(), "dg-none-" + Guid.NewGuid().ToString("N")), TimeProvider.System);
        var settings = TestSettings.Create() with { Languages = s ?? new LanguageSettings() };
        return LanguagePick.For(ctx, new OfferedLanguages(() => settings, none), saved ?? Saved(), guest, welcome);
    }

    [Fact]
    public void Cookie_wins_over_Accept_Language() => Assert.Equal("de", Pick(cookie: "de", accept: "fr"));

    [Fact]
    public void Saved_language_wins_over_Accept_Language() =>
        Assert.Equal("es", Pick(accept: "fr", saved: Saved(Gail, "es"), guest: GuestPrincipal(Gail)));

    [Fact]
    public void Cookie_wins_over_saved_language() =>
        Assert.Equal("de", Pick(cookie: "de", saved: Saved(Gail, "es"), guest: GuestPrincipal(Gail)));

    [Fact]
    public void Saved_language_is_not_used_without_a_guest() => Assert.Equal("fr", Pick(accept: "fr", saved: Saved(Gail, "es")));

    [Fact]
    public void Saved_language_that_is_unknown_is_matched_like_any_other()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dg-lang-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, $"{{\"{Gail}\":\"../../etc\"}}");
        var saved = new ResidentLanguages(file);
        Assert.Equal("../../etc", saved.Get(Gail));
        Assert.Equal("fr", Pick(accept: "fr", saved: saved, guest: GuestPrincipal(Gail)));
    }

    [Fact]
    public void Accept_Language_by_q_value() => Assert.Equal("de", Pick(accept: "fr;q=0.5, de;q=0.9"));

    [Fact]
    public void Accept_Language_regional_tag_matches_the_language() => Assert.Equal("pt", Pick(accept: "pt-BR,en;q=0.5"));

    [Fact]
    public void Malformed_Accept_Language_entries_are_skipped() => Assert.Equal("fr", Pick(accept: "de;q=abc, fr"));

    [Fact]
    public void Nothing_matches_gives_the_default() =>
        Assert.Equal("es", Pick(new LanguageSettings { Default = "es" }, accept: "ja,zh;q=0.5"));

    [Fact]
    public void An_unknown_default_gives_English() => Assert.Equal("en", Pick(new LanguageSettings { Default = "xx" }, accept: "ja"));

    [Theory]
    [InlineData("xx")]
    [InlineData("../../etc")]
    public void Junk_cookie_is_ignored(string junk) => Assert.Equal("fr", Pick(cookie: junk, accept: "fr"));

    [Fact]
    public void A_huge_cookie_is_ignored() => Assert.Equal("fr", Pick(cookie: new string('a', 5000), accept: "fr"));

    [Fact]
    public void Welcome_uses_only_lang_query_and_default()
    {
        Assert.Equal("en", Pick(cookie: "de", accept: "fr", welcome: true));
        Assert.Equal("ru", Pick(cookie: "de", accept: "fr", query: "ru", welcome: true));
    }

    [Fact]
    public void Lang_query_is_ignored_off_the_welcome_page() => Assert.Equal("fr", Pick(accept: "fr", query: "ru"));

    [Fact]
    public void Language_not_offered_is_skipped() =>
        Assert.Equal("de", Pick(new LanguageSettings { Offered = ["en", "de"] }, cookie: "fr", accept: "de"));

    [Fact]
    public async Task Staff_page_stays_English_with_a_German_cookie()
    {
        using var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        }) { Before = TestAuth.Apply };
        TestAuth.AddStaff(f.Robust);
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using (var r = await c.GetAsync("/__test/signin?role=owner", Ct)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie", "dg-lang=de");
        using var resp = await c.SendAsync(req, Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<html lang=\"en\">", html);
        // The server's culture is not touched on a staff page: the dashboard's date stays in the process culture.
        Assert.DoesNotContain("lang=\"de\"", html);
    }

    [Fact]
    public async Task The_culture_reaches_both_the_handler_and_the_render()
    {
        using var f = new LauncherFactory(s => s with { GridName = "Avalon", SetupToken = null });
        using var scope = f.Services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers.Cookie = "dg-lang=de";
        var actionCtx = new ActionContext(ctx, new RouteData(), new PageActionDescriptor());
        var filter = new ResidentLanguageFilter(welcome: false);
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            string? inHandler = null, inRender = null;
            var handlerCtx = new PageHandlerExecutingContext(new PageContext(actionCtx), [], null!, new Dictionary<string, object?>(), new object());
            await filter.OnPageHandlerExecutionAsync(handlerCtx, () =>
            {
                inHandler = $"{CultureInfo.CurrentCulture.Name}/{CultureInfo.CurrentUICulture.Name}";
                return Task.FromResult<PageHandlerExecutedContext>(null!);
            });
            CultureInfo.CurrentCulture = before.CurrentCulture;
            CultureInfo.CurrentUICulture = before.CurrentUICulture;
            await filter.OnResultExecutionAsync(new ResultExecutingContext(actionCtx, [], new EmptyResult(), new object()), () =>
            {
                inRender = $"{CultureInfo.CurrentCulture.Name}/{CultureInfo.CurrentUICulture.Name}";
                return Task.FromResult<ResultExecutedContext>(null!);
            });
            Assert.Equal("de/de", inHandler);
            Assert.Equal("de/de", inRender);
            Assert.Equal("de", ResidentLanguageFilter.Current(ctx));
        }
        finally
        {
            CultureInfo.CurrentCulture = before.CurrentCulture;
            CultureInfo.CurrentUICulture = before.CurrentUICulture;
        }
    }
}
