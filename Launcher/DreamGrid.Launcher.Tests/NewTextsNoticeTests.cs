using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Resident;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// "&lt;Language&gt;: N new texts to translate" on the dashboard and the Languages page (plan 16b, Task 7): an added language
/// that is on and misses texts, for instance after an update added some.
/// </summary>
public sealed class NewTextsNoticeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static LauncherFactory Factory(IReadOnlyList<string> offered)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Languages = s.Languages with { Offered = offered },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<IHtmlDocument> PageAsync(LauncherFactory f, string role, string url)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using (var s = await c.GetAsync($"/__test/signin?role={role}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, s.StatusCode);
        using var r = await c.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>An added language with every text but <paramref name="leaveOut"/> page texts.</summary>
    private static void AddItalian(LauncherFactory f, int leaveOut)
    {
        var catalog = f.Services.GetRequiredService<LanguageCatalog>();
        var page = catalog.PageKeys().Skip(leaveOut).ToDictionary(k => k, k => "it: " + catalog.English(k));
        var email = catalog.EmailKeys().ToDictionary(k => k, catalog.EnglishEmail);
        f.Services.GetRequiredService<OwnerLanguages>().Save(new OwnerLanguage("it", page, email, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Notice_for_added_language_with_missing_texts()
    {
        await using var f = Factory([.. Languages.BuiltIn, "it"]);
        AddItalian(f, leaveOut: 3);
        foreach (var url in new[] { "/", "/security/languages" })
        {
            var doc = await PageAsync(f, "owner", url);
            var notice = doc.QuerySelector("#new-texts");
            Assert.NotNull(notice);
            Assert.Contains("Italiano: 3 new texts to translate", notice.TextContent);
            Assert.Equal("/security/languages/it", notice.QuerySelector("a")!.GetAttribute("href"));
        }
    }

    [Fact]
    public async Task One_new_text_is_said_in_the_singular()
    {
        await using var f = Factory([.. Languages.BuiltIn, "it"]);
        AddItalian(f, leaveOut: 1);
        var doc = await PageAsync(f, "owner", "/");
        Assert.Contains("Italiano: 1 new text to translate", doc.QuerySelector("#new-texts")!.TextContent);
    }

    [Fact]
    public async Task No_notice_when_complete_or_off()
    {
        await using (var complete = Factory([.. Languages.BuiltIn, "it"]))
        {
            AddItalian(complete, leaveOut: 0);
            Assert.Null((await PageAsync(complete, "owner", "/")).QuerySelector("#new-texts"));
        }
        await using var off = Factory(Languages.BuiltIn);
        AddItalian(off, leaveOut: 3);
        Assert.Null((await PageAsync(off, "owner", "/")).QuerySelector("#new-texts"));
        Assert.Null((await PageAsync(off, "owner", "/security/languages")).QuerySelector("#new-texts"));
    }

    [Fact]
    public async Task Notice_owner_only()
    {
        await using var f = Factory([.. Languages.BuiltIn, "it"]);
        AddItalian(f, leaveOut: 3);
        // Supervisors don't have the dashboard at all; admins have it without this notice.
        Assert.Null((await PageAsync(f, "admin", "/")).QuerySelector("#new-texts"));
    }
}
