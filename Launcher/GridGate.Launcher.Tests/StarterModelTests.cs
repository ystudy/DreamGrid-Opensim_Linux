using System.Net;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Starter;
using GridGate.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GridGate.Launcher.Tests.StarterAvatarsPageTests;

namespace GridGate.Launcher.Tests;

/// <summary>Dressing and checking a model, and model accounts elsewhere in GridGate (spec 2026-11-11-starter-avatars-design §6.3, §6.5).</summary>
public sealed class StarterModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StarterAvatarList List(LauncherFactory f) => f.Services.GetRequiredService<StarterAvatarList>();

    /// <summary>A model on the fake Robust with its outfit in its inventory and, when <paramref name="worn"/>, stored as worn.</summary>
    private static (StarterAvatar Avatar, List<WornItem> Outfit) Model(LauncherFactory f, bool worn = true, string password = "old-pass-1",
        bool outfitFolder = true)
    {
        var id = Guid.NewGuid();
        f.Robust.Add("Starter01", "Avatar", 0, password, id, "");
        var root = f.Inventory.AddDefaultFolders(id);
        var folder = outfitFolder ? Guid.NewGuid() : root;
        if (outfitFolder) f.Inventory.AddFolder(id, new InventoryFolder(folder, root, "Lyra", -1));
        var shape = f.Inventory.Add(id, folder, "Shape", assetType: 13);
        var hair = f.Inventory.Add(id, folder, "Hair", assetType: 6);
        var outfit = new List<WornItem> { new("Wearable 0:0", shape.ItemId), new("_ap_2", hair.ItemId) };
        if (worn) f.Appearance.Set(id, outfit);
        var a = List(f).Add("Lyra", id, "Starter01", "Avatar", "Lyra/FULLAVATARS/Lyra complete female avatar");
        return (a, outfit);
    }

    [Fact]
    public async Task Login_sets_a_password_shown_once_and_unreadies()
    {
        await using var f = Factory();
        var (a, _) = Model(f);
        List(f).Update(a.Id, x => x with { CheckPassed = true, Snapshot = "s.webp" });
        var owner = await SignedInAsync(f);
        using var r = await PostAsync(owner, "Login", new() { ["id"] = a.Id.ToString() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        var shown = page.QuerySelector("#model-login")!;
        Assert.Contains("Starter01 Avatar", shown.TextContent);
        Assert.Contains("Lyra/FULLAVATARS/Lyra complete female avatar", shown.TextContent);
        Assert.Contains("Replace Outfit", shown.TextContent);
        var password = shown.QuerySelector("code.model-password")!.TextContent.Trim();
        Assert.Equal(16, password.Length);
        Assert.True(await f.Robust.AuthenticateAsync(a.Model, password, Ct));
        Assert.False(List(f).All().Single().CheckPassed);
        Assert.DoesNotContain(password, await owner.GetStringAsync(Url, Ct)); // shown once only
        Assert.Contains(Audit(f), e => e.Action == "starter-avatar-login" && e.Target == "Lyra");
    }

    [Fact]
    public async Task Check_pass_makes_ready_and_rotates_password()
    {
        await using var f = Factory();
        var (a, _) = Model(f);
        List(f).Update(a.Id, x => x with { Snapshot = "s.webp" });
        var owner = await SignedInAsync(f);
        using var r = await PostAsync(owner, "Check", new() { ["id"] = a.Id.ToString() });
        var after = List(f).All().Single();
        Assert.True(after.CheckPassed);
        Assert.NotNull(after.CheckedAt);
        Assert.True(after.IsReady);
        Assert.False(await f.Robust.AuthenticateAsync(a.Model, "old-pass-1", Ct));
        Assert.Contains(Audit(f), e => e.Action == "starter-avatar-checked" && e.Result == "passed");
    }

    [Fact]
    public async Task Check_fail_lists_missing_slots()
    {
        await using var f = Factory();
        var (a, outfit) = Model(f, worn: false);
        f.Appearance.Set(a.Model, [.. outfit, new WornItem("Wearable 5:0", Guid.NewGuid())]); // a jacket the model doesn't own
        var owner = await SignedInAsync(f);
        using var r = await PostAsync(owner, "Check", new() { ["id"] = a.Id.ToString() });
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Wearable 5:0", page.QuerySelector("#check-result")!.TextContent);
        Assert.False(List(f).All().Single().CheckPassed);
        Assert.True(await f.Robust.AuthenticateAsync(a.Model, "old-pass-1", Ct)); // not rotated: the owner may still be dressing it
        Assert.Contains(Audit(f), e => e.Action == "starter-avatar-checked" && e.Result.StartsWith("failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_fails_while_the_model_wears_items_outside_its_folder()
    {
        await using var f = Factory();
        var (a, _) = Model(f, outfitFolder: false); // worn items in My Inventory, as Robust's default outfit is
        var owner = await SignedInAsync(f);
        using var r = await PostAsync(owner, "Check", new() { ["id"] = a.Id.ToString() });
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Replace Outfit", page.QuerySelector("#check-result")!.TextContent);
        Assert.False(List(f).All().Single().CheckPassed);
    }

    [Fact]
    public async Task Check_clears_an_old_warning()
    {
        await using var f = Factory();
        var (a, _) = Model(f);
        List(f).Update(a.Id, x => x with { Warning = "Lyra wasn't copied to a new resident; check its model" });
        var owner = await SignedInAsync(f);
        (await PostAsync(owner, "Check", new() { ["id"] = a.Id.ToString() })).Dispose();
        Assert.Null(List(f).All().Single().Warning);
    }

    [Fact]
    public async Task Model_cannot_be_deleted_from_people()
    {
        await using var f = Factory();
        var (a, _) = Model(f);
        Assert.Equal("This account is a starter avatar model.", f.Services.GetRequiredService<AccountHolds>().Refusal(a.Model));
    }

    [Fact]
    public async Task Model_cannot_sign_in_to_resident_pages()
    {
        await using var f = Factory();
        var (a, _) = Model(f);
        var c = f.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        var page = new HtmlParser().ParseDocument(await c.GetStringAsync("/guest/sign-in", Ct));
        var form = new Dictionary<string, string>
        {
            ["Name"] = "Starter01 Avatar", ["Password"] = "old-pass-1",
            ["__RequestVerificationToken"] = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        };
        using var r = await c.PostAsync("/guest/sign-in", new FormUrlEncodedContent(form), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // the same page as a wrong password, never signed in
        Assert.False(r.Headers.Contains("Set-Cookie") && r.Headers.GetValues("Set-Cookie").Any(v => v.Contains("guest", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task People_list_tags_models_and_person_shows_the_pick()
    {
        await using var f = Factory();
        var (a, _) = Model(f);
        var bea = Guid.NewGuid();
        f.Robust.Add("Bea", "Example", 0, "x", bea, "");
        List(f).Picked(new StarterPick(bea, a.Id, "Lyra", DateTimeOffset.UtcNow));
        var owner = await SignedInAsync(f);
        var people = new HtmlParser().ParseDocument(await owner.GetStringAsync("/people?q=Starter01", Ct));
        Assert.Contains("Starter avatar", people.QuerySelector($"tr[data-id='{a.Model}']")!.TextContent);
        var person = new HtmlParser().ParseDocument(await owner.GetStringAsync($"/people/{bea}", Ct));
        Assert.Contains("Lyra", person.QuerySelector("#person-details")!.TextContent);
    }

    [Fact]
    public void Dashboard_does_not_count_models() =>
        Assert.Contains("REGEXP '^Starter[0-9]{2}$'", GridGate.Launcher.Core.Dashboard.WorldCounter.Person);
}
