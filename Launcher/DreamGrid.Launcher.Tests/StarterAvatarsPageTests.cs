using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Starter;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's Starter avatars page, /security/starter-avatars (spec 2026-11-11-starter-avatars-design §6).</summary>
public sealed class StarterAvatarsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Url = "/security/starter-avatars";

    internal static LauncherFactory Factory(OwnerPlan? plan = OwnerPlan.Local)
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
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    internal static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner")
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var r = await c.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    internal static async Task<string> TokenAsync(HttpClient c)
    {
        var page = new HtmlParser().ParseDocument(await c.GetStringAsync(Url, Ct));
        return page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
    }

    internal static async Task<HttpResponseMessage> PostAsync(HttpClient c, string handler, Dictionary<string, string> form)
    {
        form["__RequestVerificationToken"] = await TokenAsync(c);
        return await c.PostAsync($"{Url}?handler={handler}", new FormUrlEncodedContent(form), Ct);
    }

    internal static List<(string Action, string Target, string Result)> Audit(LauncherFactory f, string prefix = "starter-avatar")
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return [.. new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("action").GetString()!.StartsWith(prefix, StringComparison.Ordinal))
            .Select(e => (e.GetProperty("action").GetString()!, e.GetProperty("target").GetString() ?? "", e.GetProperty("result").GetString()!))];
    }

    private static StarterAvatarList List(LauncherFactory f) => f.Services.GetRequiredService<StarterAvatarList>();

    [Theory]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Owner_sees_the_page_admin_does_not(string role)
    {
        await using var f = Factory();
        using (var owner = await SignedInAsync(f))
        using (var ok = await owner.GetAsync(Url, Ct))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var other = await SignedInAsync(f, role);
        using var get = await other.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task Not_on_a_region_server()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        var owner = await SignedInAsync(f);
        using var get = await owner.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Page_lists_each_avatar_with_its_checklist()
    {
        await using var f = Factory();
        List(f).Add("Lyra", Guid.NewGuid(), "Starter01", "Avatar");
        var owner = await SignedInAsync(f);
        var page = new HtmlParser().ParseDocument(await owner.GetStringAsync(Url, Ct));
        var row = page.QuerySelector(".starter-row")!;
        Assert.Contains("Lyra", row.TextContent);
        Assert.Contains("Starter01 Avatar", row.TextContent);
        Assert.Contains("Not on the sign-up page", row.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='/security/starter-avatars']") ?? page.QuerySelector("h1"));
    }

    [Fact]
    public async Task Snapshot_upload_is_saved_and_audited()
    {
        await using var f = Factory();
        var lyra = List(f).Add("Lyra", Guid.NewGuid(), "Starter01", "Avatar");
        var owner = await SignedInAsync(f);
        using var content = new MultipartFormDataContent
        {
            { new StringContent(await TokenAsync(owner)), "__RequestVerificationToken" },
            { new StringContent(lyra.Id.ToString()), "id" },
        };
        var file = new ByteArrayContent(StarterSnapshotsTests.Png(1600, 1200));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "lyra.png");
        using var r = await owner.PostAsync($"{Url}?handler=Snapshot", content, Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var saved = List(f).All().Single();
        Assert.Equal(lyra.Id + ".webp", saved.Snapshot);
        Assert.True(File.Exists(Path.Combine(f.Paths.StarterAvatars, saved.Snapshot!)));
        Assert.Contains(Audit(f), e => e.Action == "starter-avatar-snapshot" && e.Target == "Lyra");
    }

    [Fact]
    public async Task Arrange_actions_are_audited()
    {
        await using var f = Factory();
        var a = List(f).Add("Lyra", Guid.NewGuid(), "Starter01", "Avatar");
        var b = List(f).Add("Leo", Guid.NewGuid(), "Starter02", "Avatar");
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Move", new() { ["id"] = b.Id.ToString(), ["delta"] = "-1" })).Dispose();
        (await PostAsync(owner, "Default", new() { ["id"] = b.Id.ToString() })).Dispose();
        (await PostAsync(owner, "Rename", new() { ["id"] = a.Id.ToString(), ["name"] = "  Lyra Rose  " })).Dispose();
        (await PostAsync(owner, "Hide", new() { ["id"] = a.Id.ToString() })).Dispose();
        (await PostAsync(owner, "Show", new() { ["id"] = a.Id.ToString() })).Dispose();
        using (var bad = await PostAsync(owner, "Rename", new() { ["id"] = a.Id.ToString(), ["name"] = new string('x', 65) }))
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode); // shown again with the problem

        Assert.Equal(["Leo", "Lyra Rose"], List(f).All().Select(x => x.Name));
        Assert.Equal(b.Id, List(f).DefaultId);
        Assert.False(List(f).All().Single(x => x.Id == a.Id).Hidden);
        Assert.Equal(["starter-avatar-order", "starter-avatar-default", "starter-avatar-renamed", "starter-avatar-hidden", "starter-avatar-shown"],
            Audit(f).Select(e => e.Action));
    }

    [Fact]
    public async Task Remove_with_delete_model_deletes_the_account()
    {
        await using var f = Factory();
        var model = Guid.NewGuid();
        var keep = Guid.NewGuid();
        var a = List(f).Add("Lyra", model, "Starter01", "Avatar");
        var b = List(f).Add("Leo", keep, "Starter02", "Avatar");
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Remove", new() { ["id"] = a.Id.ToString(), ["deleteModel"] = "true" })).Dispose();
        (await PostAsync(owner, "Remove", new() { ["id"] = b.Id.ToString() })).Dispose();
        Assert.Empty(List(f).All());
        Assert.Equal([model], f.Remover.Deleted);
        Assert.Equal([("starter-avatar-removed", "Lyra", "model deleted"), ("starter-avatar-removed", "Leo", "ok")], Audit(f));
    }
}
