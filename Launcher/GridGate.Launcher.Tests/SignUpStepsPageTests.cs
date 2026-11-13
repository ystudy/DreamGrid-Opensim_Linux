using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Starter;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Step 2 of signing up, choosing a starter avatar (spec 2026-11-11-starter-avatars-design §5.1, §5.2).</summary>
public sealed class SignUpStepsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient Client(LauncherFactory f) =>
        f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost") });

    private static async Task<IHtmlDocument> GetAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static StarterAvatar AddReady(LauncherFactory f, string name, int n, bool snapshotFile = true)
    {
        var list = f.Services.GetRequiredService<StarterAvatarList>();
        var a = list.Add(name, Guid.NewGuid(), $"Starter{n:00}", "Avatar");
        list.Update(a.Id, x => x with { Snapshot = x.Id + ".webp", CheckPassed = true, CheckedAt = DateTimeOffset.UnixEpoch });
        if (snapshotFile)
        {
            Directory.CreateDirectory(f.Paths.StarterAvatars);
            File.WriteAllBytes(Path.Combine(f.Paths.StarterAvatars, a.Id + ".webp"), "RIFF0000WEBPVP8 "u8.ToArray());
        }
        return list.All().Single(x => x.Id == a.Id);
    }

    private static Dictionary<string, string> Form(IHtmlDocument page, string? avatar, string first = "Bea") => new()
    {
        ["First"] = first, ["Last"] = "Example", ["Email"] = "bea@example.com", ["Password"] = "tall-green-kettle",
        ["Again"] = "tall-green-kettle", ["website"] = "", ["AcceptTerms"] = "true",
        ["__RequestVerificationToken"] = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        ["Avatar"] = avatar ?? "",
    };

    [Fact]
    public async Task No_starters_no_step_two()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        var page = await GetAsync(Client(rig.F), "/signup");
        Assert.Null(page.QuerySelector("#step2"));
        Assert.Empty(page.QuerySelectorAll("input[name=Avatar]"));
        Assert.Equal("Create my avatar", page.QuerySelector("#sign-up button[type=submit]")!.TextContent.Trim());
    }

    [Fact]
    public async Task Ready_starters_are_radios_with_default_checked()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        var lyra = AddReady(rig.F, "Lyra", 1);
        var leo = AddReady(rig.F, "Leo", 2);
        rig.F.Services.GetRequiredService<StarterAvatarList>().SetDefault(leo.Id);
        rig.F.Services.GetRequiredService<StarterAvatarList>().Add("Not ready", Guid.NewGuid(), "Starter03", "Avatar");

        var page = await GetAsync(Client(rig.F), "/signup");
        Assert.NotNull(page.QuerySelector("#step2"));
        Assert.Equal("Choose your avatar", page.QuerySelector("#step2 h2")!.TextContent.Trim());
        var radios = page.QuerySelectorAll("#step2 input[type=radio][name=Avatar]").Cast<IHtmlInputElement>().ToList();
        Assert.Equal([lyra.Id.ToString(), leo.Id.ToString()], radios.Select(r => r.Value));
        Assert.Equal(leo.Id.ToString(), Assert.Single(radios, r => r.IsChecked).Value);
        var img = page.QuerySelector($"img[src='/starter-avatars/{lyra.Id}.webp']")!;
        Assert.Equal("Lyra", img.GetAttribute("alt"));
        Assert.Equal("lazy", img.GetAttribute("loading"));
        Assert.NotNull(page.QuerySelector("#sign-up button#to-step2"));
        Assert.NotNull(page.QuerySelector("script[src='/js/signup-steps.js']"));
    }

    [Fact]
    public async Task Refused_post_keeps_the_pick_and_shows_step_one()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        AddReady(rig.F, "Lyra", 1);
        var leo = AddReady(rig.F, "Leo", 2);
        var client = Client(rig.F);
        var page = await GetAsync(client, "/signup");
        using var r = await client.PostAsync("/signup", new FormUrlEncodedContent(Form(page, leo.Id.ToString(), first: "")), Ct);
        var back = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.NotNull(back.QuerySelector(".error-box"));
        Assert.Equal("1", back.QuerySelector("#sign-up")!.GetAttribute("data-step"));
        Assert.Equal(leo.Id.ToString(),
            back.QuerySelectorAll("input[name=Avatar]").Cast<IHtmlInputElement>().Single(x => x.IsChecked).Value);
    }

    [Fact]
    public async Task Snapshot_served_only_for_ready_avatars()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        var lyra = AddReady(rig.F, "Lyra", 1);
        var client = Client(rig.F);
        using (var ok = await client.GetAsync($"/starter-avatars/{lyra.Id}.webp", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("image/webp", ok.Content.Headers.ContentType!.MediaType);
        }
        rig.F.Services.GetRequiredService<StarterAvatarList>().Update(lyra.Id, a => a with { Hidden = true });
        using (var hidden = await client.GetAsync($"/starter-avatars/{lyra.Id}.webp", Ct))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var unknown = await client.GetAsync($"/starter-avatars/{Guid.NewGuid()}.webp", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Post_with_avatar_reaches_signups()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        AddReady(rig.F, "Lyra", 1);
        var leo = AddReady(rig.F, "Leo", 2);
        var client = Client(rig.F);
        var page = await GetAsync(client, "/signup");
        using var r = await client.PostAsync("/signup", new FormUrlEncodedContent(Form(page, leo.Id.ToString())), Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("Starter02 Avatar", rig.F.Grid.LastModel);
    }
}
