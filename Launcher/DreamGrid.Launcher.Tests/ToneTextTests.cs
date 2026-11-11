using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Resident;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's tone text and added languages reach the resident pages (plan 16b, task 3).</summary>
public sealed class ToneTextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherFactory Factory(Dictionary<string, Dictionary<string, string>>? wording = null, string[]? offered = null) =>
        new(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Wording = wording?.ToDictionary(l => l.Key, l => (IReadOnlyDictionary<string, string>)l.Value),
            Languages = new LanguageSettings { Offered = offered ?? Languages.BuiltIn },
        })
        {
            Before = TestAuth.Apply,
        };

    private static async Task<string> HtmlAsync(LauncherFactory f, string url, string? lang)
    {
        using var client = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (lang is not null) req.Headers.Add("Cookie", "dg-lang=" + lang);
        using var r = await client.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync(Ct);
    }

    private static async Task<IHtmlDocument> PageAsync(LauncherFactory f, string url, string? lang) =>
        new HtmlParser().ParseDocument(await HtmlAsync(f, url, lang));

    private static Dictionary<string, Dictionary<string, string>> Wording(string lang, string key, string text) =>
        new() { [lang] = new() { [key] = text } };

    private static string IntroOf(IHtmlDocument page) => page.QuerySelector("p.tone-text")!.TextContent.Trim();

    private static string SiteCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.Launcher", "wwwroot", "css", "site.css")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "DreamGrid.Launcher", "wwwroot", "css", "site.css"));
    }

    [Fact]
    public void ToFormat_escapes_braces_and_turns_grid_into_the_first_argument()
    {
        Assert.Equal("Hi {0}! {{x}} {{0}}", ToneText.ToFormat("Hi {grid}! {x} {0}"));
        Assert.Equal("Hi Avalon {x}", string.Format(ToneText.ToFormat("Hi {grid} {x}"), "Avalon"));
        Assert.Equal("{", string.Format(ToneText.ToFormat("{"), "Avalon"));
    }

    [Fact]
    public void Keys_are_the_tone_keys_each_with_a_label()
    {
        Assert.DoesNotContain("ResetPassword.Intro", ToneText.Keys);
        Assert.Contains("Home.Intro", ToneText.Keys);
        Assert.All(ToneText.Keys, k => Assert.False(string.IsNullOrWhiteSpace(ToneText.Title(k))));
    }

    [Fact]
    public async Task Owner_override_wins_in_its_language()
    {
        await using var f = Factory(Wording("de", "Home.Intro", "Willkommen bei {grid}!"));
        Assert.Equal("Willkommen bei Avalon!", IntroOf(await PageAsync(f, "/home", "de")));
    }

    [Fact]
    public async Task Owner_override_does_not_leak_to_other_languages()
    {
        await using var f = Factory(Wording("de", "Home.Intro", "Willkommen bei {grid}!"));
        var fr = IntroOf(await PageAsync(f, "/home", "fr"));
        Assert.DoesNotContain("Willkommen", fr);
        Assert.NotEqual(IntroOf(await PageAsync(f, "/home", "en")), fr);
    }

    [Fact]
    public async Task Blank_override_means_built_in()
    {
        await using var f = Factory(Wording("de", "Home.Intro", "   "));
        using var plain = Factory();
        Assert.Equal(IntroOf(await PageAsync(plain, "/home", "de")), IntroOf(await PageAsync(f, "/home", "de")));
        Assert.NotEmpty(IntroOf(await PageAsync(f, "/home", "de")));
    }

    [Fact]
    public async Task Override_with_braces_and_html_renders_literally()
    {
        await using var f = Factory(Wording("de", "Home.Intro", "Hi {0} {x} <b>&"));
        var html = await HtmlAsync(f, "/home", "de");
        Assert.Contains("Hi {0} {x} &lt;b&gt;&amp;", html);
        var page = new HtmlParser().ParseDocument(html);
        Assert.Equal("Hi {0} {x} <b>&", IntroOf(page));
        Assert.Null(page.QuerySelector("p.tone-text b"));
    }

    [Fact]
    public async Task Paragraphs_keep_their_line_breaks()
    {
        await using var f = Factory(Wording("de", "Home.Intro", "Eins\n\nZwei"));
        var p = (await PageAsync(f, "/home", "de")).QuerySelector("p.tone-text")!;
        Assert.Contains("Eins\n\nZwei", p.TextContent.Replace("\r\n", "\n"));
        Assert.Null(p.GetAttribute("style"));
        Assert.Matches(@"\.tone-text\s*\{[^}]*white-space:\s*pre-line", SiteCss());
    }

    [Fact]
    public async Task Added_language_text_is_used()
    {
        await using var f = Factory(offered: [.. Languages.BuiltIn, "it"]);
        f.Services.GetRequiredService<OwnerLanguages>().Save(new OwnerLanguage("it",
            new Dictionary<string, string> { ["Common.SignIn"] = "Accedi" }, new Dictionary<string, string>(), default));
        var page = await PageAsync(f, "/guest/sign-in", "it");
        Assert.Equal("Accedi", page.QuerySelector("h1")?.TextContent.Trim());
    }

    [Fact]
    public async Task Added_language_missing_key_falls_back_to_English()
    {
        await using var f = Factory(offered: [.. Languages.BuiltIn, "it"]);
        f.Services.GetRequiredService<OwnerLanguages>().Save(new OwnerLanguage("it",
            new Dictionary<string, string> { ["Common.SignIn"] = "Accedi" }, new Dictionary<string, string>(), default));
        var page = await PageAsync(f, "/guest/sign-in", "it");
        Assert.Contains("Use the name and password of your avatar.", page.Body!.TextContent);
    }

    [Fact]
    public async Task Non_tone_keys_ignore_owner_wording()
    {
        await using var f = Factory(Wording("de", "Common.SignIn", "GEHEIM"));
        var page = await PageAsync(f, "/guest/sign-in", "de");
        Assert.DoesNotContain("GEHEIM", page.Body!.TextContent);
    }

    [Fact]
    public async Task Staff_pages_are_unaffected()
    {
        await using var f = Factory(Wording("de", "Home.Intro", "Willkommen bei {grid}!"));
        TestAuth.AddStaff(f.Robust);
        using var client = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost") });
        using var signIn = await client.GetAsync("/__test/signin?role=owner", Ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie", "dg-lang=de");
        using var r = await client.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain("Willkommen", await r.Content.ReadAsStringAsync(Ct));
    }
}
