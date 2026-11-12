using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Starter;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Starter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Sign-up passing the resident's starter avatar to Robust, and checking the copy (spec 2026-11-11-starter-avatars-design §5).</summary>
public sealed class StarterSignUpTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("dg-startersu-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static StarterAvatar AddReady(StarterAvatarList list, string name, int n, Guid? model = null)
    {
        var a = list.Add(name, model ?? Guid.NewGuid(), $"Starter{n:00}", "Avatar");
        list.Update(a.Id, x => x with { Snapshot = x.Id + ".webp", CheckPassed = true, CheckedAt = DateTimeOffset.UnixEpoch });
        return list.All().Single(x => x.Id == a.Id);
    }

    private static StarterAvatarList Starters(SignUpsTests.Rig rig) => rig.F.Services.GetRequiredService<StarterAvatarList>();

    [Fact]
    public async Task Picked_avatar_model_reaches_create_user()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        AddReady(Starters(rig), "Lyra", 1);
        var leo = AddReady(Starters(rig), "Leo", 2);
        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form() with { Avatar = leo.Id }, null, Ct, "en");
        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.Equal("Starter02 Avatar", rig.F.Grid.LastModel);
        Assert.Equal("Leo", Starters(rig).PickOf(r.Account!.PrincipalId)!.Name);
    }

    [Fact]
    public async Task Stale_pick_falls_back_to_default()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        var lyra = AddReady(Starters(rig), "Lyra", 1);
        var leo = AddReady(Starters(rig), "Leo", 2);
        Starters(rig).SetDefault(lyra.Id);
        Starters(rig).Update(leo.Id, a => a with { Hidden = true }); // hidden after the page was opened
        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form() with { Avatar = leo.Id }, null, Ct, "en");
        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.Equal("Starter01 Avatar", rig.F.Grid.LastModel);
    }

    [Fact]
    public async Task No_ready_avatar_sends_no_model()
    {
        await using var rig = SignUpsTests.Factory(email: false);
        Starters(rig).Add("Not ready", Guid.NewGuid(), "Starter01", "Avatar");
        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form() with { Avatar = Guid.NewGuid() }, null, Ct, "en");
        Assert.Equal(SignUpOutcome.SignedUp, r.Outcome);
        Assert.Null(rig.F.Grid.LastModel);
    }

    [Fact]
    public async Task Decoy_gets_the_model_and_the_check_too()
    {
        await using var rig = SignUpsTests.Factory();
        var lyra = AddReady(Starters(rig), "Lyra", 1);
        // ann@example.com is already on the grid: a decoy is made, with the same model and the same check.
        var r = await rig.SignUps.SubmitAsync(SignUpsTests.Form(email: "ann@example.com"), null, Ct, "en");
        Assert.Equal(SignUpOutcome.CheckEmail, r.Outcome);
        Assert.Equal("Starter01 Avatar", rig.F.Grid.LastModel);
        for (var i = 0; i < 200 && !rig.F.Appearance.Asked.Contains(lyra.Model); i++) await Task.Delay(25, Ct);
        Assert.Contains(lyra.Model, rig.F.Appearance.Asked);
    }

    // ---- the copy check on its own ----

    private sealed record CheckRig(StarterCopyCheck Check, StarterAvatarList List, FakeAppearanceStore Looks, FakeInventoryStore Inventory,
        AuditLog Audit, StarterAvatar Avatar, Guid Resident, List<WornItem> ModelWorn) : IDisposable
    {
        public void Dispose() => Audit.Dispose();
    }

    private CheckRig MakeCheck()
    {
        var robust = new FakeRobustAccounts();
        var inv = new FakeInventoryStore(robust);
        var looks = new FakeAppearanceStore();
        var list = new StarterAvatarList(Path.Combine(_dir, "avatars.json"), TimeProvider.System);
        var audit = new AuditLog(Path.Combine(_dir, "audit.log"), TimeProvider.System);
        var model = Guid.NewGuid();
        var resident = Guid.NewGuid();
        var mf = inv.AddDefaultFolders(model);
        inv.AddDefaultFolders(resident);
        var shape = inv.Add(model, mf, "Shape", assetType: 13);
        var hair = inv.Add(model, mf, "Hair", assetType: 6);
        var worn = new List<WornItem> { new("Wearable 0:0", shape.ItemId), new("_ap_2", hair.ItemId) };
        looks.Set(model, worn);
        var avatar = AddReady(list, "Lyra", 1, model);
        var check = new StarterCopyCheck(looks, inv, list, audit, TimeProvider.System, NullLogger<StarterCopyCheck>.Instance)
        {
            Wait = TimeSpan.FromMilliseconds(600),
            Poll = TimeSpan.FromMilliseconds(20),
        };
        return new CheckRig(check, list, looks, inv, audit, avatar, resident, worn);
    }

    /// <summary>The resident's copy: new items with the model's assets, worn in the same slots.</summary>
    private static List<WornItem> CopyTo(CheckRig r)
    {
        var folder = r.Inventory.AddDefaultFolders(Guid.NewGuid()); // any folder id of the resident's will do for the fake
        var worn = new List<WornItem>();
        foreach (var w in r.ModelWorn)
        {
            var src = r.Inventory.Items.Single(i => i.ItemId == w.ItemId);
            var copy = r.Inventory.Add(r.Resident, folder, src.Name!, src.AssetId, src.AssetType ?? 0);
            worn.Add(w with { ItemId = copy.ItemId });
        }
        return worn;
    }

    [Fact]
    public async Task Check_waits_for_a_slow_copy()
    {
        using var r = MakeCheck();
        var copy = CopyTo(r);
        var asks = 0;
        r.Looks.Set(r.Resident, () => ++asks <= 2 ? null : copy);
        await r.Check.RunAsync(r.Resident, r.Avatar, Ct);
        Assert.True(asks >= 3);
        Assert.Contains(r.List.Ready(), a => a.Id == r.Avatar.Id);
        Assert.Empty(r.Audit.Recent(e => e.Entry.Action == "starter-avatar-failed", 10));
    }

    [Fact]
    public async Task Failed_copy_takes_the_avatar_off_sign_up()
    {
        using var r = MakeCheck();
        var copy = CopyTo(r);
        copy.RemoveAll(w => w.Slot == "_ap_2"); // half-dressed: Robust left the hair slot empty
        r.Looks.Set(r.Resident, copy);
        await r.Check.RunAsync(r.Resident, r.Avatar, Ct);
        Assert.DoesNotContain(r.List.Ready(), a => a.Id == r.Avatar.Id);
        Assert.Equal("Lyra wasn't copied to a new resident; check its model", r.List.All().Single().Warning);
        var e = Assert.Single(r.Audit.Recent(e => e.Entry.Action == "starter-avatar-failed", 10));
        Assert.Equal("Lyra", e.Entry.Target);
    }

    [Fact]
    public async Task Database_error_leaves_the_avatar_alone()
    {
        using var r = MakeCheck();
        r.Looks.Down = true;
        await r.Check.RunAsync(r.Resident, r.Avatar, Ct);
        Assert.Contains(r.List.Ready(), a => a.Id == r.Avatar.Id);
    }
}
