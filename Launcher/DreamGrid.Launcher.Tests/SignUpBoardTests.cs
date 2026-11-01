using DreamGrid.Launcher.Core.Guests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The sign-ups board: every sign-up with its state (spec 2026-11-01-sign-up-approval-design §3).</summary>
public sealed class SignUpBoardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-board-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "guests", "pending-signups.json");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SignUpBoard Board() => new(File_, _time);

    [Fact]
    public void Add_and_get_round_trip()
    {
        var a = Guid.NewGuid();
        var board = Board();
        board.Add(a, "Ann Example", SignUpState.WaitingForEmail);

        var e = board.Get(a)!;
        Assert.Equal("Ann Example", e.Name);
        Assert.Equal(SignUpState.WaitingForEmail, e.State);
        Assert.Equal(_time.GetUtcNow(), e.Made);
        Assert.Equal(_time.GetUtcNow(), e.LastLink);
        Assert.True(e.Waiting);
        Assert.Null(board.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Linked_keeps_made()
    {
        var a = Guid.NewGuid();
        var board = Board();
        var made = _time.GetUtcNow();
        board.Add(a, "Ann Example", SignUpState.WaitingForEmail);
        _time.Advance(TimeSpan.FromHours(3));
        board.Linked(a);

        var e = board.Get(a)!;
        Assert.Equal(made, e.Made);
        Assert.Equal(_time.GetUtcNow(), e.LastLink);
    }

    [Fact]
    public void Contains_waiting_and_unlisted_only()
    {
        var board = Board();
        var states = Enum.GetValues<SignUpState>().ToDictionary(s => s, _ => Guid.NewGuid());
        foreach (var (state, id) in states) board.Add(id, state.ToString(), state);

        foreach (var (state, id) in states)
            Assert.Equal(state is SignUpState.WaitingForEmail or SignUpState.WaitingForStaff or SignUpState.Unlisted, board.Contains(id));
        board.Remove(states[SignUpState.Unlisted]);
        Assert.False(board.Contains(states[SignUpState.Unlisted]));
    }

    [Fact]
    public void TryFinish_once_only()
    {
        var a = Guid.NewGuid();
        var board = Board();
        board.Add(a, "Ann Example", SignUpState.WaitingForStaff);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(board.TryFinish(a, SignUpState.Approved, "Fred Owner"));
        Assert.False(board.TryFinish(a, SignUpState.SelfApproved, null));
        Assert.False(board.TryFinish(Guid.NewGuid(), SignUpState.Approved, "Fred Owner"));

        var e = board.Get(a)!;
        Assert.Equal(SignUpState.Approved, e.State);
        Assert.Equal("Fred Owner", e.By);
        Assert.Equal(_time.GetUtcNow(), e.Changed);
    }

    [Fact]
    public void TryFinish_unlisted_only_to_self_approved()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var board = Board();
        board.Add(a, "Ann Example", SignUpState.Unlisted);
        board.Add(b, "Bo Example", SignUpState.Unlisted);

        Assert.False(board.TryFinish(a, SignUpState.Approved, "Fred Owner"));
        Assert.False(board.TryFinish(a, SignUpState.Denied, "Fred Owner"));
        Assert.True(board.TryFinish(b, SignUpState.SelfApproved, null));
        Assert.Equal(SignUpState.Unlisted, board.Get(a)!.State);
    }

    [Fact]
    public void Shown_hides_unlisted()
    {
        var shown = Guid.NewGuid();
        var board = Board();
        board.Add(Guid.NewGuid(), "Hidden One", SignUpState.Unlisted);
        board.Add(shown, "Ann Example", SignUpState.WaitingForEmail);

        Assert.Equal([shown], board.Shown().Select(e => e.Principal));
        Assert.Equal(1, board.WaitingCount());
    }

    [Fact]
    public void ToDelete_waiting_after_30_days_from_made()
    {
        var a = Guid.NewGuid();
        var board = Board();
        board.Add(a, "Ann Example", SignUpState.WaitingForEmail);
        _time.Advance(TimeSpan.FromDays(29));
        board.Linked(a);
        Assert.Empty(board.ToDelete(TimeSpan.FromHours(24)));

        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.Equal([a], board.ToDelete(TimeSpan.FromHours(24)).Select(e => e.Principal));
    }

    [Fact]
    public void ToDelete_unlisted_after_24_hours_from_last_link()
    {
        var a = Guid.NewGuid();
        var board = Board();
        board.Add(a, "Ann Example", SignUpState.Unlisted);
        _time.Advance(TimeSpan.FromHours(20));
        board.Linked(a);
        _time.Advance(TimeSpan.FromHours(20));
        Assert.Empty(board.ToDelete(TimeSpan.FromHours(24)));

        _time.Advance(TimeSpan.FromHours(5));
        Assert.Equal([a], board.ToDelete(TimeSpan.FromHours(24)).Select(e => e.Principal));
    }

    [Fact]
    public void PruneFinished_after_14_days()
    {
        var approved = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        var board = Board();
        board.Add(approved, "Ann Example", SignUpState.WaitingForStaff);
        board.Add(denied, "Bo Example", SignUpState.WaitingForStaff);
        board.Add(waiting, "Cy Example", SignUpState.WaitingForStaff);
        board.TryFinish(approved, SignUpState.Approved, "Fred Owner");
        board.TryFinish(denied, SignUpState.Denied, "Fred Owner");

        _time.Advance(TimeSpan.FromDays(13));
        board.PruneFinished();
        Assert.Equal(3, board.Shown().Count);

        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        board.PruneFinished();
        Assert.Equal([waiting], board.Shown().Select(e => e.Principal));
    }

    [Fact]
    public void Old_format_reads_as_unlisted()
    {
        var a = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, $"{{ \"{a}\": \"{when:O}\" }}");

        var board = Board();
        var e = board.Get(a)!;
        Assert.Equal(SignUpState.Unlisted, e.State);
        Assert.Equal("", e.Name);
        Assert.Equal(when, e.Made);
        Assert.Equal(when, e.LastLink);
        Assert.True(board.Contains(a));
        Assert.Empty(board.Shown());

        board.SetName(a, "Ann Example");
        Assert.Equal("Ann Example", Board().Get(a)!.Name);
    }

    [Fact]
    public void MarkGreeted_is_kept()
    {
        var a = Guid.NewGuid();
        var board = Board();
        board.Add(a, "Ann Example", SignUpState.WaitingForStaff);
        board.TryFinish(a, SignUpState.Approved, "Fred Owner");
        Assert.False(board.Get(a)!.Greeted);

        board.MarkGreeted(a);
        Assert.True(Board().Get(a)!.Greeted);
    }

    [Fact]
    public void Survives_restart()
    {
        var a = Guid.NewGuid();
        Board().Add(a, "Ann Example", SignUpState.WaitingForStaff);

        Assert.Equal(SignUpState.WaitingForStaff, Board().Get(a)!.State);
    }

    [Fact]
    public void File_holds_no_email()
    {
        Board().Add(Guid.NewGuid(), "Ann Example", SignUpState.WaitingForEmail);

        Assert.DoesNotContain("@", File.ReadAllText(File_));
    }

    [Fact]
    public void A_damaged_file_reads_empty_and_is_set_aside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "not json");
        var board = Board();
        Assert.Empty(board.Shown());

        board.Add(Guid.NewGuid(), "Ann Example", SignUpState.WaitingForEmail);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!, "pending-signups.json.damaged-*"));
    }
}
