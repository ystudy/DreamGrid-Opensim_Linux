using System.Security.Claims;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Email;

namespace DreamGrid.Launcher.Security;

/// <summary>What a takedown step did, and the case it was on.</summary>
public sealed record TakedownOutcome(bool Done, string Message, string? Number);

/// <summary>Someone else holding a copy of a removed item, and why they can't be ticked (null when they can).</summary>
public sealed record CopyRow(InventoryHolding Holding, string? Refusal);

/// <summary>
/// The takedown steps (spec 2026-11-04-inventory-takedown-design): take items down into a new or open case, find and take down
/// copies others hold, put an item back, add a note, close. Only the owner and admins, on residents (<see cref="PersonTask.Inventory"/>),
/// who are offline. The record and the evidence copy are saved before anything leaves the database. Each step is audited with
/// names and the case number only; the notice, notes and reasons stay in the case.
/// </summary>
public sealed class TakedownActions(PeopleActions people, IInventoryStore inventory, TakedownStore store, EvidenceCopier evidence,
    EmailSender sender, AuditLog audit, TimeProvider time, ILogger<TakedownActions> log)
{
    public const int CopiesShown = 200;
    public const string Closed = "This case is closed.";
    public const string NoSuchCase = "There is no such case.";
    public const string NotThere = "That item isn't in their inventory any more.";
    public const string WhyNeeded = "Say why, in a few words.";
    public const string Deleted = "They have been deleted, so it can't be put back.";
    public const string NothingPicked = "Tick at least one item.";
    public const string NotSaved = "The case couldn't be saved, so nothing was removed. Check the log.";

    private static bool MayAct(ClaimsPrincipal actor) => StaffSignIn.RoleOf(actor) is StaffRole.Owner or StaffRole.Admin;

    public async Task<TakedownOutcome> TakeDownAsync(ClaimsPrincipal actor, string? ip, Guid person, IReadOnlyCollection<Guid> items,
        string? intoCase, TakedownNotice? notice, bool tell, CancellationToken ct)
    {
        var adding = intoCase is not null;
        var action = adding ? "takedown-added" : "takedown-opened";
        TakedownCase? existing = null;
        if (adding)
        {
            existing = store.Get(intoCase!);
            if (existing is null) return new(false, NoSuchCase, null);
            if (!existing.Open) return new(false, Closed, existing.Number);
        }
        else
        {
            if (notice is null) return new(false, "Who sent the notice is needed.", null);
            notice = Clean(notice);
            if (TakedownRules.NoticeProblem(notice) is { } bad) return new(false, bad, null);
        }
        if (items.Count == 0) return new(false, NothingPicked, intoCase);

        var (view, problem) = await CheckPersonAsync(actor, ip, person, action, existing?.Number, ct);
        if (view is null) return new(false, problem!, intoCase);

        IReadOnlyList<InventoryItemView> found;
        try
        {
            found = await inventory.ItemsAsync(person, items, ct);
        }
        catch (StaffDirectoryUnavailableException)
        {
            return new(false, PeopleActions.DatabaseDown, intoCase);
        }
        if (found.Count != items.Distinct().Count()) return new(false, NotThere, intoCase);

        var (who, id, _) = AuditActor.Of(actor);
        var now = time.GetUtcNow();
        TakedownCase c;
        if (existing is null)
        {
            var reason = notice!.Reason;
            try
            {
                c = store.Open(notice, tell && TakedownRules.MayTell(reason), number => new TakedownCase(number, true, notice, false, [], [],
                    [new CaseEvent(now, who, id, "opened", null)], null));
            }
            catch (IOException e)
            {
                log.LogWarning("Takedowns: a case couldn't be opened ({Error}).", e.GetType().Name);
                return new(false, NotSaved, null);
            }
        }
        else c = existing;

        var (done, message) = await RemoveIntoAsync(c, view, PersonOnCase.Named, found, actor, ip, ct);
        var target = LogText.Clean($"{view.FullName}: {c.Number}", 140);
        Write(actor, ip, action, target, done ? "ok" : "failed");
        return new(done, message, c.Number);
    }

    /// <summary>The copies others hold of the case's items, or null when the database couldn't be asked (nothing is recorded then).</summary>
    public async Task<IReadOnlyList<CopyRow>?> FindCopiesAsync(ClaimsPrincipal actor, string? ip, string number, CancellationToken ct)
    {
        if (!MayAct(actor) || store.Get(number) is not { Open: true } c) return [];
        var onCase = c.Items.Select(i => i.ItemId).ToHashSet();
        var rows = new List<CopyRow>();
        var views = new Dictionary<Guid, (PersonView? View, string? Problem)>();
        foreach (var asset in c.Items.Where(i => i.AssetId is { Length: > 0 }).Select(i => i.AssetId!).Distinct())
        {
            IReadOnlyList<InventoryHolding> holdings;
            try
            {
                holdings = await inventory.CopiesAsync(asset, CopiesShown, ct);
            }
            catch (StaffDirectoryUnavailableException)
            {
                Write(actor, ip, "takedown-copies", c.Number, "failed");
                return null;
            }
            foreach (var h in holdings.Where(h => !onCase.Contains(h.Item.Row.ItemId)))
            {
                if (!views.TryGetValue(h.Owner, out var v)) views[h.Owner] = v = await people.LookAsync(actor, h.Owner, ct);
                var refusal = v.View is null ? v.Problem ?? PeopleActions.Gone
                    : people.Refusal(actor, v.View, PersonTask.Inventory) ?? (v.View.Row.OnlineRegion is null ? null : PeopleActions.InWorld);
                rows.Add(new CopyRow(h, refusal));
            }
        }
        var (who, id, _) = AuditActor.Of(actor);
        var searched = new CaseEvent(time.GetUtcNow(), who, id, "copies-searched", $"{rows.Count} found");
        store.Update(number, now => now with { Events = [.. now.Events, searched] });
        Write(actor, ip, "takedown-copies", c.Number, "ok");
        return rows;
    }

    public async Task<TakedownOutcome> TakeCopiesAsync(ClaimsPrincipal actor, string? ip, string number,
        IReadOnlyCollection<(Guid Owner, Guid Item)> picks, CancellationToken ct)
    {
        if (!MayAct(actor))
        {
            Write(actor, ip, "takedown-added", number, "refused");
            return new(false, PeopleRules.SupervisorOnly, number);
        }
        if (store.Get(number) is not { } first) return new(false, NoSuchCase, null);
        if (!first.Open) return new(false, Closed, number);
        if (picks.Count == 0) return new(false, NothingPicked, number);
        var said = new List<string>();
        var allDone = true;
        foreach (var group in picks.GroupBy(p => p.Owner))
        {
            var (view, problem) = await CheckPersonAsync(actor, ip, group.Key, "takedown-added", number, ct);
            if (view is null) { allDone = false; said.Add(problem!); continue; }
            IReadOnlyList<InventoryItemView> found;
            try
            {
                found = await inventory.ItemsAsync(group.Key, [.. group.Select(g => g.Item)], ct);
            }
            catch (StaffDirectoryUnavailableException)
            {
                return new(false, PeopleActions.DatabaseDown, number);
            }
            if (found.Count == 0) { allDone = false; said.Add($"{view.FullName}: {NotThere}"); continue; }
            var (done, message) = await RemoveIntoAsync(store.Get(number)!, view, PersonOnCase.CopyHolder, found, actor, ip, ct);
            Write(actor, ip, "takedown-added", LogText.Clean($"{view.FullName}: {number}", 140), done ? "ok" : "failed");
            allDone &= done;
            said.Add(message);
        }
        return new(allDone, string.Join(" ", said), number);
    }

    public async Task<TakedownOutcome> PutBackAsync(ClaimsPrincipal actor, string? ip, string number, Guid item, string? why, CancellationToken ct)
    {
        if (!MayAct(actor))
        {
            Write(actor, ip, "takedown-restored", number, "refused");
            return new(false, PeopleRules.SupervisorOnly, number);
        }
        if (store.Get(number) is not { } c) return new(false, NoSuchCase, null);
        if (!c.Open) return new(false, Closed, number);
        if (c.Items.FirstOrDefault(i => i.ItemId == item) is not { } entry) return new(false, NotThere, number);
        if (entry.PutBackAt is not null) return new(false, $"{entry.Name} is already back.", number);
        why = Limit(why);
        if (why.Length == 0) return new(false, WhyNeeded, number);

        var (view, problem) = await CheckPersonAsync(actor, ip, entry.Holder, "takedown-restored", number, ct);
        if (view is null) return new(false, problem == PeopleActions.Gone ? Deleted : problem!, number);
        Guid? into = null;
        try
        {
            // Not the request's token: once the row is written, the case must say so.
            into = await inventory.PutBackAsync(entry.Row, CancellationToken.None);
        }
        catch (NoInventoryException)
        {
            return new(false, Deleted, number);
        }
        catch (Exception e) when (e is StaffDirectoryUnavailableException or OperationCanceledException)
        {
            // The answer may have been lost after the row went in: look before saying it failed.
            if (!await IsInInventoryAsync(entry.Holder, item)) return new(false, PeopleActions.DatabaseDown, number);
        }
        var folder = "their inventory";
        try
        {
            if (into is { } f && (await inventory.FoldersAsync(entry.Holder, CancellationToken.None)).FirstOrDefault(x => x.Id == f) is { } found)
                folder = found.Type == 8 ? "My Inventory" : found.Name;
        }
        catch (StaffDirectoryUnavailableException)
        {
            // Only the folder's name is missing from the message.
        }
        var (who, id, _) = AuditActor.Of(actor);
        var now = time.GetUtcNow();
        List<CaseEvent> added = [new(now, who, id, "put-back", why)];
        if (c.TellResident && Email(view, EmailTemplates.TakedownRestored, c, [entry.Name])) added.Add(new(now, who, id, "emailed", view.FullName));
        var saved = store.Update(number, cur => cur with
        {
            Items = [.. cur.Items.Select(i => i.ItemId == item && i.PutBackAt is null ? i with { PutBackAt = now } : i)],
            Events = [.. cur.Events, .. added],
        }) is not null;
        if (!saved) log.LogWarning("Takedowns: case {Number} couldn't record a put-back that happened.", number);
        Write(actor, ip, "takedown-restored", LogText.Clean($"{view.FullName}: {number} {entry.Name}", 140), saved ? "ok" : "failed");
        return new(true, $"Put back {entry.Name} in {folder}.", number);
    }

    public TakedownOutcome AddNote(ClaimsPrincipal actor, string? ip, string number, string? text) =>
        Change(actor, ip, number, "takedown-note", text, c => c, "note", "Note added.");

    public TakedownOutcome Close(ClaimsPrincipal actor, string? ip, string number, string? outcome) =>
        Change(actor, ip, number, "takedown-closed", outcome, c => c with { Open = false, Outcome = Limit(outcome) }, "closed", $"Case {number} is closed.");

    private TakedownOutcome Change(ClaimsPrincipal actor, string? ip, string number, string action, string? text,
        Func<TakedownCase, TakedownCase> change, string what, string said)
    {
        if (!MayAct(actor))
        {
            Write(actor, ip, action, number, "refused");
            return new(false, PeopleRules.SupervisorOnly, number);
        }
        if (store.Get(number) is not { } c) return new(false, NoSuchCase, null);
        if (!c.Open) return new(false, Closed, number);
        var why = Limit(text);
        if (why.Length == 0) return new(false, WhyNeeded, number);
        var (who, id, _) = AuditActor.Of(actor);
        var happened = new CaseEvent(time.GetUtcNow(), who, id, what, why);
        var closedMeanwhile = false;
        var saved = store.Update(number, cur =>
        {
            if (!cur.Open) { closedMeanwhile = true; return null; }
            return change(cur) with { Events = [.. cur.Events, happened] };
        });
        if (closedMeanwhile) return new(false, Closed, number);
        if (saved is null)
        {
            Write(actor, ip, action, number, "failed");
            return new(false, "The case couldn't be saved. Check the log.", number);
        }
        Write(actor, ip, action, number, "ok");
        return new(true, said, number);
    }

    /// <summary>
    /// Copies the evidence, saves the case with the items (and the person) on it, then removes them from the inventory. When the
    /// removal fails, the items come back off the case so it never claims what didn't happen.
    /// </summary>
    private async Task<(bool Done, string Message)> RemoveIntoAsync(TakedownCase c, PersonView person, PersonOnCase kind,
        IReadOnlyList<InventoryItemView> found, ClaimsPrincipal actor, string? ip, CancellationToken ct)
    {
        var (who, staffId, _) = AuditActor.Of(actor);
        var principal = person.Row.Account.PrincipalId;
        var now = time.GetUtcNow();
        var folder = store.CaseFolder(c.Number);
        var added = new List<CaseItem>();
        foreach (var v in found)
        {
            var r = v.Row;
            string? hash = null;
            try
            {
                if (r.AssetId is { Length: > 0 }) hash = await inventory.AssetHashAsync(r.AssetId, ct);
            }
            catch (StaffDirectoryUnavailableException)
            {
                // The copy says why below; taking the item out of reach matters more.
            }
            var e = r.AssetId is { Length: > 0 } ? evidence.Copy(hash, r.AssetId, folder) : new EvidenceResult(null, null, EvidenceCopier.NoRecord);
            added.Add(new CaseItem(r.ItemId, principal, r.Name ?? "", r.Description, r.AssetType, r.CreatorId, v.CreatorName, v.FolderPath, r.AssetId, r,
                e.File, e.Sha256, e.NotKept, now, null));
        }
        var removedEvents = added.Select(i => new CaseEvent(now, who, staffId, "removed", $"{person.FullName}: {i.Name}")).ToList();
        var newPerson = new CasePerson(principal, person.FullName, kind);
        var withItems = store.Update(c.Number, cur => cur.Open ? cur with
        {
            People = cur.People.Any(p => p.Principal == principal) ? cur.People : [.. cur.People, newPerson],
            Items = [.. cur.Items, .. added],
            Events = [.. cur.Events, .. removedEvents],
        } : null);
        if (withItems is null) return (false, store.Get(c.Number) is { Open: false } ? Closed : NotSaved);
        var ids = added.Select(i => i.ItemId).ToList();
        try
        {
            // Not the request's token: a closed tab must not leave the case and the inventory disagreeing.
            await inventory.RemoveAsync(principal, ids, CancellationToken.None);
        }
        catch (StaffDirectoryUnavailableException e)
        {
            log.LogWarning("Takedowns: items couldn't be removed: {Message}", e.InnerException?.Message ?? e.Message);
            // The answer may have been lost after the commit: keep on the case every item that really left the inventory.
            IReadOnlyList<Guid>? still = null;
            try
            {
                still = [.. (await inventory.ItemsAsync(principal, ids, CancellationToken.None)).Select(v => v.Row.ItemId)];
            }
            catch (StaffDirectoryUnavailableException)
            {
                log.LogWarning("Takedowns: case {Number} may list items still in the inventory: the database stopped answering.", c.Number);
            }
            if (still is not null && still.Count > 0)
            {
                var keep = still.ToHashSet();
                var hadOthers = c.People.Any(p => p.Principal == principal);
                store.Update(c.Number, cur => cur with
                {
                    Items = [.. cur.Items.Where(i => !(i.Holder == principal && keep.Contains(i.ItemId) && i.RemovedAt == now))],
                    Events = [.. cur.Events.Where(ev => !removedEvents.Contains(ev) || !keep.Contains(added[removedEvents.IndexOf(ev)].ItemId))],
                    People = still.Count == ids.Count && !hadOthers ? [.. cur.People.Where(p => p.Principal != principal)] : cur.People,
                });
                return (false, PeopleActions.DatabaseDown);
            }
            if (still is null) return (false, PeopleActions.DatabaseDown);
        }
        foreach (var i in added)
            Write(actor, ip, "takedown-removed", LogText.Clean($"{person.FullName}: {c.Number} {i.Name}", 140), "ok");
        if (withItems.TellResident && Email(person, EmailTemplates.TakedownRemoved, withItems, [.. added.Select(i => i.Name)]))
        {
            var emailed = new CaseEvent(now, who, staffId, "emailed", person.FullName);
            store.Update(c.Number, cur => cur with { Events = [.. cur.Events, emailed] });
        }
        return (true, $"Removed {added.Count} item(s) from {person.FullName}. Case {c.Number}.");
    }

    private async Task<bool> IsInInventoryAsync(Guid holder, Guid item)
    {
        try
        {
            return (await inventory.ItemsAsync(holder, [item], CancellationToken.None)).Count > 0;
        }
        catch (StaffDirectoryUnavailableException)
        {
            return false;
        }
    }

    private bool Email(PersonView person, string template, TakedownCase c, IReadOnlyList<string> items)
    {
        if (!TakedownRules.MayTell(c.Notice.Reason) || person.Row.Account.Email.Trim() is not { Length: > 0 } to) return false;
        return sender.Queue(template, to, person.FullName, new Dictionary<string, string>
        {
            ["AvatarName"] = person.FullName,
            ["CaseNumber"] = c.Number,
            ["Items"] = string.Join(", ", items),
            ["Reason"] = TakedownRules.ReasonWords(c.Notice.Reason),
        }) == QueueResult.Queued;
    }

    /// <summary>The person, after the role, the rules and the in-world check; or null with the sentence (audited as refused).</summary>
    private async Task<(PersonView? View, string? Problem)> CheckPersonAsync(ClaimsPrincipal actor, string? ip, Guid principal, string action,
        string? number, CancellationToken ct)
    {
        var (view, problem) = await people.LookAsync(actor, principal, ct);
        if (view is null) return (null, problem ?? PeopleActions.Gone);
        var target = LogText.Clean(number is null ? view.FullName : $"{view.FullName}: {number}", 140);
        var refusal = MayAct(actor) ? people.Refusal(actor, view, PersonTask.Inventory) : PeopleRules.SupervisorOnly;
        if (refusal is null && view.Row.OnlineRegion is not null) refusal = PeopleActions.InWorld;
        if (refusal is null) return (view, null);
        Write(actor, ip, action, target, "refused");
        return (null, refusal);
    }

    private static string Limit(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length > TakedownRules.MaxField ? t[..TakedownRules.MaxField] : t;
    }

    private static TakedownNotice Clean(TakedownNotice n) => n with
    {
        SenderName = (n.SenderName ?? "").Trim(),
        Organisation = Blank(n.Organisation),
        Address = Blank(n.Address),
        Reference = Blank(n.Reference),
        Text = (n.Text ?? "").Trim(),
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void Write(ClaimsPrincipal actor, string? ip, string action, string target, string result)
    {
        var (who, id, role) = AuditActor.Of(actor);
        audit.Write(new AuditEntry(who, id, role, ip, action, target, result));
    }
}
