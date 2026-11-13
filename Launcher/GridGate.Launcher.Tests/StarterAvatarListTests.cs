using GridGate.Launcher.Core.Starter;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The owner's list of starter avatars (spec 2026-11-11-starter-avatars-design §6.4, §6.6).</summary>
public sealed class StarterAvatarListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-starter-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private string File => Path.Combine(_dir, "avatars.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private StarterAvatarList List() => new(File, _time);

    private static StarterAvatar ReadyUp(StarterAvatar a) => a with { Snapshot = a.Id + ".webp", CheckPassed = true, CheckedAt = DateTimeOffset.UnixEpoch };

    private StarterAvatar AddReady(StarterAvatarList list, string name, int n)
    {
        var a = list.Add(name, Guid.NewGuid(), $"Starter{n:00}", "Avatar");
        list.Update(a.Id, ReadyUp);
        return list.All().Single(x => x.Id == a.Id);
    }

    [Fact]
    public void Resolve_returns_the_pick_when_ready()
    {
        var list = List();
        AddReady(list, "Lyra", 1);
        var leo = AddReady(list, "Leo", 2);
        Assert.Equal(leo.Id, list.Resolve(leo.Id)!.Id);
    }

    [Fact]
    public void Resolve_falls_back_to_default_then_first_ready_then_null()
    {
        var list = List();
        var lyra = AddReady(list, "Lyra", 1);
        var leo = AddReady(list, "Leo", 2);
        var nash = AddReady(list, "Nash", 3);
        var rana = AddReady(list, "Rana", 4);
        Assert.True(list.SetDefault(leo.Id));

        list.Update(nash.Id, a => a with { Hidden = true });
        Assert.Equal(leo.Id, list.Resolve(nash.Id)!.Id);
        list.Update(rana.Id, a => a with { Warning = "not copied" });
        Assert.Equal(leo.Id, list.Resolve(rana.Id)!.Id);
        Assert.Equal(leo.Id, list.Resolve(Guid.NewGuid())!.Id);
        Assert.Equal(leo.Id, list.Resolve(null)!.Id);

        list.Update(leo.Id, a => a with { CheckPassed = false });
        Assert.Equal(lyra.Id, list.Resolve(null)!.Id);
        list.Update(lyra.Id, a => a with { Snapshot = null });
        Assert.Null(list.Resolve(null));
        Assert.Empty(list.Ready());
    }

    [Fact]
    public void Default_is_first_ready_when_none_chosen()
    {
        var list = List();
        list.Add("Not ready", Guid.NewGuid(), "Starter01", "Avatar");
        var leo = AddReady(list, "Leo", 2);
        Assert.Null(list.DefaultId);
        Assert.Equal(leo.Id, list.Default()!.Id);
    }

    [Fact]
    public void Move_swaps_order_and_survives_reload()
    {
        var list = List();
        var a = AddReady(list, "A", 1);
        var b = AddReady(list, "B", 2);
        var c = AddReady(list, "C", 3);
        Assert.True(list.Move(c.Id, -1));
        Assert.False(list.Move(a.Id, -1));
        Assert.True(list.SetDefault(b.Id));
        var again = List();
        Assert.Equal(["A", "C", "B"], again.All().Select(x => x.Name));
        Assert.Equal(b.Id, again.DefaultId);
        Assert.True(again.Remove(c.Id));
        Assert.Equal(["A", "B"], List().All().Select(x => x.Name));
    }

    [Fact]
    public void Next_skips_taken_numbers()
    {
        Assert.Equal(("Starter02", "Avatar"), StarterNames.Next(["Starter01", "Starter03"]));
        Assert.Equal(("Starter01", "Avatar"), StarterNames.Next([]));
        Assert.Equal(("Starter02", "Avatar"), StarterNames.Next(["starter01"]));
        Assert.True(StarterNames.IsModelName("starter07", "AVATAR"));
        Assert.False(StarterNames.IsModelName("Starter7", "Avatar"));
        Assert.False(StarterNames.IsModelName("Starter07", "Avatars"));
    }

    [Fact]
    public void Picks_are_kept_per_principal()
    {
        var list = List();
        var lyra = AddReady(list, "Lyra", 1);
        var ann = Guid.NewGuid();
        list.Picked(new StarterPick(ann, lyra.Id, "Lyra", _time.GetUtcNow()));
        Assert.Equal("Lyra", List().PickOf(ann)!.Name);
        Assert.Null(List().PickOf(Guid.NewGuid()));
        Assert.True(List().IsModel(lyra.Model));
        Assert.False(List().IsModel(ann));
    }
}
