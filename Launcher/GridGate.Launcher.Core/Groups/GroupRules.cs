namespace GridGate.Launcher.Core.Groups;

/// <summary>
/// What an admin may save as a group's name and charter, and how a member ID in OpenSim's group tables is read
/// (spec 2026-11-02-groups-admin-design). A plain UUID is a resident of this grid; anything else is a hypergrid visitor,
/// stored as <c>uuid;home url;First Last</c>.
/// </summary>
public static class GroupRules
{
    /// <summary>The viewer's limit for a group name.</summary>
    public const int MaxName = 35;

    /// <summary>The column is <c>text</c>; the viewer's own box is smaller, so this only stops a runaway paste.</summary>
    public const int MaxCharter = 4000;

    /// <summary>The Everyone role every member has (OpenSim's <c>UUID.Zero</c>), also "no active group".</summary>
    public static readonly Guid Everyone = Guid.Empty;

    public static string? NameProblem(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) return "Type a group name.";
        if (n.Length > MaxName) return $"A group name can be at most {MaxName} characters.";
        if (n.Any(char.IsControl)) return "A group name can't contain line breaks or tabs.";
        if (n.Any(char.IsSurrogate)) return "A group name can't contain emoji or other symbols the grid can't store.";
        return null;
    }

    public static string? CharterProblem(string? charter)
    {
        if ((charter?.Length ?? 0) > MaxCharter) return $"The charter can be at most {MaxCharter} characters.";
        // Name and Charter are 3-byte utf8 columns: MariaDB refuses a 4-byte character (emoji and the like).
        if (charter is not null && charter.Any(char.IsSurrogate)) return "The charter can't contain emoji or other symbols the grid can't store.";
        return null;
    }

    /// <summary>Whether <paramref name="principalId"/> is a resident of this grid (a plain UUID), and which.</summary>
    public static bool IsLocal(string principalId, out Guid id) => Guid.TryParse(principalId, out id) && !principalId.Contains(';');

    /// <summary>
    /// The name to show for a member: a resident's account name (or the ID when the account is gone), or a visitor's name from
    /// their ID followed by " (visitor)".
    /// </summary>
    public static string DisplayName(string principalId, string? first, string? last)
    {
        if (IsLocal(principalId, out _))
            return string.IsNullOrWhiteSpace(first) ? principalId : $"{first} {last}".Trim();
        var parts = principalId.Split(';');
        var name = parts.Length >= 3 && parts[2].Trim().Length > 0 ? parts[2].Trim() : parts[0];
        return name + " (visitor)";
    }
}
