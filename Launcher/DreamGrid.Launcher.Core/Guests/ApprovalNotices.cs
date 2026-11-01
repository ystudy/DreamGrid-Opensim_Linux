using System.Text.Json;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// What the "sign-ups waiting" email to approvers needs to remember (spec 2026-11-01-sign-up-approval-design §8): which staff turned it
/// off, when it last went out, and the newest sign-up it covered. Kept in <c>file</c> (<c>&lt;data&gt;/guests/approval-notices.json</c>,
/// written atomically); PrincipalIDs and times only. A missing or damaged file reads as nobody opted out and nothing sent. Safe to call
/// from any thread.
/// </summary>
public sealed class ApprovalNotices(string file)
{
    private sealed record State(List<Guid> OptedOut, DateTimeOffset? LastSent, DateTimeOffset NewestNoticed);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    public bool OptedOut(Guid staff)
    {
        lock (_gate) return Read().OptedOut.Contains(staff);
    }

    public void SetOptedOut(Guid staff, bool value)
    {
        lock (_gate)
        {
            var s = Read();
            var list = s.OptedOut.Where(g => g != staff).ToList();
            if (value) list.Add(staff);
            Write(s with { OptedOut = list });
        }
    }

    /// <summary>When the email last went out, or null if never.</summary>
    public DateTimeOffset? LastSent
    {
        get { lock (_gate) return Read().LastSent; }
    }

    /// <summary>The made time of the newest sign-up the last email covered.</summary>
    public DateTimeOffset NewestNoticed
    {
        get { lock (_gate) return Read().NewestNoticed; }
    }

    /// <summary>The email went out on the pass that started <paramref name="at"/>, covering sign-ups made up to <paramref name="newestMade"/>.</summary>
    public void Sent(DateTimeOffset at, DateTimeOffset newestMade)
    {
        lock (_gate) Write(Read() with { LastSent = at, NewestNoticed = newestMade });
    }

    private State Read()
    {
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(file), Options) ?? Empty();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or NotSupportedException)
        {
            return Empty();
        }
    }

    private static State Empty() => new([], null, DateTimeOffset.MinValue);

    private void Write(State s)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(s, Options));
    }
}
