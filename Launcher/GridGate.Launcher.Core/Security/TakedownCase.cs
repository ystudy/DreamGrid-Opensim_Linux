using GridGate.Launcher.Core.Database;

namespace GridGate.Launcher.Core.Security;

/// <summary>Why a notice asks for a takedown.</summary>
public enum TakedownReason { Copyright, Illegal, Rules, Other }

/// <summary>Whether the notice is about this person, or they only held a copy of the thing (often an innocent buyer).</summary>
public enum PersonOnCase { Named, CopyHolder }

/// <summary>The notice as it arrived: who sent it, their reference, when, and what it says.</summary>
public sealed record TakedownNotice(TakedownReason Reason, string SenderName, string? Organisation, string? Address, string? Reference,
    DateOnly Received, string Text);

/// <summary>A person on a case, with their name at the time.</summary>
public sealed record CasePerson(Guid Principal, string Name, PersonOnCase Kind);

/// <summary>
/// An item taken down: what it was, who held it and where, its whole inventory row (for a put-back), and the evidence copy (its
/// file in the case folder and SHA-256) or why there is none.
/// </summary>
public sealed record CaseItem(Guid ItemId, Guid Holder, string Name, string? Description, int? AssetType, string CreatorId, string CreatorName,
    string FolderPath, string? AssetId, InventoryItemRow Row, string? EvidenceFile, string? EvidenceSha256, string? NotKept,
    DateTimeOffset RemovedAt, DateTimeOffset? PutBackAt);

/// <summary>
/// One thing that happened on a case, by whom, with the why where there is one. <see cref="What"/> is <c>opened</c>,
/// <c>removed</c>, <c>copies-searched</c>, <c>put-back</c>, <c>note</c>, <c>emailed</c>, <c>closed</c> or
/// <see cref="TakedownCase.UploadMatch"/>.
/// </summary>
public sealed record CaseEvent(DateTimeOffset At, string Staff, Guid? StaffId, string What, string? Why);

/// <summary>A takedown case (spec 2026-11-04-inventory-takedown-design): kept in <c>case.json</c> in its own folder.</summary>
public sealed record TakedownCase(string Number, bool Open, TakedownNotice Notice, bool TellResident, IReadOnlyList<CasePerson> People,
    IReadOnlyList<CaseItem> Items, IReadOnlyList<CaseEvent> Events, string? Outcome)
{
    /// <summary>
    /// The event added when taken-down content comes back in an upload and is removed (spec 2026-11-11-iar-backups-design §6.4): the
    /// only change a closed case takes, and it never reopens it.
    /// </summary>
    public const string UploadMatch = "upload-match";

    /// <summary>A closed case that an upload has matched since it was closed (the case list says so).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MatchedSinceClosed
    {
        get
        {
            if (Open) return false;
            var closed = -1;
            for (var i = 0; i < Events.Count; i++)
                if (Events[i].What == "closed") closed = i;
            return Events.Skip(closed + 1).Any(e => e.What == UploadMatch);
        }
    }
}
