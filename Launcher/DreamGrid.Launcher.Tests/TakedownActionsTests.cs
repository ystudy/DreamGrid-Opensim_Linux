using System.Security.Claims;
using System.Text.Json;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Taking items down, finding copies, putting back and closing (spec 2026-11-04-inventory-takedown-design).</summary>
public sealed class TakedownActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid AnnId = Guid.Parse("1a000000-0000-0000-0000-00000000001a");
    private static readonly Guid BobId = Guid.Parse("1b000000-0000-0000-0000-00000000001b");
    private static readonly Guid RegionId = Guid.Parse("0c000000-0000-0000-0000-00000000000c");
    private const string Asset = "5a5a5a5a-0000-0000-0000-000000000001";

    internal sealed record Rig(LauncherFactory F, EmailPageTests.FakeTransport Transport, Guid AnnRoot) : IAsyncDisposable
    {
        public TakedownActions Actions => F.Services.GetRequiredService<TakedownActions>();
        public TakedownStore Store => F.Services.GetRequiredService<TakedownStore>();
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    internal static Rig Factory()
    {
        var transport = new EmailPageTests.FakeTransport();
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(transport),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Holder", 0, "pass-word1", AnnId, email: "ann@example.com");
        f.Robust.Add("Bob", "Copier", 0, "pass-word1", BobId, email: "bob@example.com");
        var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
            "grid@gmail.com", null, 500, null, false, 0, null, null);
        f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        var root = f.Inventory.AddDefaultFolders(AnnId);
        f.Inventory.AddDefaultFolders(BobId);
        return new Rig(f, transport, root);
    }

    internal static ClaimsPrincipal As(StaffRole role)
    {
        var a = TestAuth.Account(role);
        List<Claim> claims =
        [
            new(ClaimTypes.Name, $"{a.FirstName} {a.LastName}"),
            new(StaffSignIn.PrincipalClaim, a.PrincipalId.ToString()),
            .. StaffSignIn.RoleClaims(role).Select(r => new Claim(ClaimTypes.Role, r)),
            new(StaffSignIn.SessionClaim, "test"),
            new(StaffSignIn.StampClaim, "test"),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Admin));
    }

    internal static TakedownNotice Notice(TakedownReason reason = TakedownReason.Copyright) =>
        new(reason, "Rita Rights", "Rights Ltd", "rita@example.com", "R-1", new DateOnly(2026, 11, 3), "The secret sword text.");

    private static async Task<List<(string To, string Subject)>> SentAsync(Rig rig, int atLeast, int waits = 200)
    {
        for (var i = 0; i < waits; i++)
        {
            lock (rig.Transport.Sent)
                if (rig.Transport.Sent.Count >= atLeast) return [.. rig.Transport.Sent.Select(m => (m.To, m.Email.Subject))];
            await Task.Delay(25, Ct);
        }
        lock (rig.Transport.Sent) return [.. rig.Transport.Sent.Select(m => (m.To, m.Email.Subject))];
    }

    private static List<JsonElement> Audit(LauncherFactory f, string? action = null)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => action is null || e.GetProperty("action").GetString() == action).ToList();
    }

    private static async Task<(TakedownOutcome Outcome, Core.Database.InventoryItemRow Item)> TakeSwordAsync(Rig rig,
        TakedownReason reason = TakedownReason.Copyright, bool tell = true)
    {
        var sword = rig.F.Inventory.Add(AnnId, rig.AnnRoot, "Sword", Asset, 6);
        var outcome = await rig.Actions.TakeDownAsync(As(StaffRole.Admin), null, AnnId, [sword.ItemId], null, Notice(reason), tell, Ct);
        return (outcome, sword);
    }

    [Fact]
    public async Task Taking_down_removes_the_items_saves_the_case_and_copies_evidence()
    {
        await using var rig = Factory();
        var hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        rig.F.Inventory.Hashes[Asset] = hash;
        var dir = Path.Combine(rig.F.Paths.FsAssetsData, "ab", "cd", "ef", "0123");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, hash + ".gz"), [1, 2, 3]);

        var (outcome, sword) = await TakeSwordAsync(rig);

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal("Removed 1 item(s) from Ann Holder. Case TD-" + DateTimeOffset.UtcNow.Year + "-001.", outcome.Message);
        Assert.DoesNotContain(rig.F.Inventory.Items, i => i.ItemId == sword.ItemId);
        Assert.Equal(2, rig.F.Inventory.VersionOf(rig.AnnRoot));
        var c = rig.Store.Get(outcome.Number!)!;
        Assert.True(c.Open);
        var item = Assert.Single(c.Items);
        Assert.Equal(sword, item.Row);
        Assert.Equal("Ann Holder", Assert.Single(c.People).Name);
        Assert.Equal(PersonOnCase.Named, c.People[0].Kind);
        Assert.NotNull(item.EvidenceSha256);
        Assert.True(File.Exists(Path.Combine(rig.Store.CaseFolder(c.Number), item.EvidenceFile!)));
        Assert.Contains(c.Events, e => e.What == "opened");
        Assert.Contains(c.Events, e => e.What == "removed");
        Assert.Equal("ok", Assert.Single(Audit(rig.F, "takedown-opened")).GetProperty("result").GetString());
        Assert.Equal($"Ann Holder: {c.Number} Sword", Assert.Single(Audit(rig.F, "takedown-removed")).GetProperty("target").GetString());
    }

    [Fact]
    public async Task In_world_person_is_refused_and_nothing_changes()
    {
        await using var rig = Factory();
        rig.F.People.Online[AnnId] = RegionId;

        var (outcome, sword) = await TakeSwordAsync(rig);

        Assert.False(outcome.Done);
        Assert.Equal(PeopleActions.InWorld, outcome.Message);
        Assert.Contains(rig.F.Inventory.Items, i => i.ItemId == sword.ItemId);
        Assert.Empty(rig.Store.All());
    }

    [Fact]
    public async Task Supervisor_is_refused_and_audited()
    {
        await using var rig = Factory();
        var sword = rig.F.Inventory.Add(AnnId, rig.AnnRoot, "Sword", Asset, 6);

        var outcome = await rig.Actions.TakeDownAsync(As(StaffRole.Supervisor), null, AnnId, [sword.ItemId], null, Notice(), true, Ct);

        Assert.Equal(PeopleRules.SupervisorOnly, outcome.Message);
        Assert.Equal("refused", Assert.Single(Audit(rig.F, "takedown-opened")).GetProperty("result").GetString());
        Assert.Empty(rig.Store.All());
    }

    [Fact]
    public async Task Database_down_leaves_no_removed_item_in_the_case()
    {
        await using var rig = Factory();
        var sword = rig.F.Inventory.Add(AnnId, rig.AnnRoot, "Sword", Asset, 6);
        var store = rig.F.Inventory;
        // Down only for the delete: reading the items worked.
        var actions = rig.Actions;
        rig.F.Inventory.FailRemove = true;

        var outcome = await actions.TakeDownAsync(As(StaffRole.Admin), null, AnnId, [sword.ItemId], null, Notice(), true, Ct);

        Assert.False(outcome.Done);
        Assert.Equal(PeopleActions.DatabaseDown, outcome.Message);
        Assert.Contains(store.Items, i => i.ItemId == sword.ItemId);
        Assert.All(rig.Store.All(), c => Assert.Empty(c.Items));
    }

    [Fact]
    public async Task Copyright_case_emails_the_resident_once()
    {
        await using var rig = Factory();
        await TakeSwordAsync(rig);
        var sent = await SentAsync(rig, 1);
        Assert.Equal([("ann@example.com", "Items removed from your Avalon inventory")], sent);
        Assert.Contains(rig.Store.All()[0].Events, e => e.What == "emailed");
    }

    [Fact]
    public async Task Illegal_content_never_emails_even_when_tell_is_posted()
    {
        await using var rig = Factory();
        var (outcome, _) = await TakeSwordAsync(rig, TakedownReason.Illegal, tell: true);
        Assert.True(outcome.Done, outcome.Message);
        Assert.Empty(await SentAsync(rig, 1, waits: 20));
        Assert.False(rig.Store.Get(outcome.Number!)!.TellResident);
    }

    [Fact]
    public async Task Adding_to_an_open_case_keeps_its_number()
    {
        await using var rig = Factory();
        var (first, _) = await TakeSwordAsync(rig);
        var shield = rig.F.Inventory.Add(AnnId, rig.AnnRoot, "Shield", null, 6);

        var again = await rig.Actions.TakeDownAsync(As(StaffRole.Admin), null, AnnId, [shield.ItemId], first.Number, null, true, Ct);

        Assert.True(again.Done, again.Message);
        Assert.Equal(first.Number, again.Number);
        Assert.Equal(2, rig.Store.Get(first.Number!)!.Items.Count);
        Assert.Single(rig.Store.All());
        Assert.Single(Audit(rig.F, "takedown-added"));
    }

    [Fact]
    public async Task Find_copies_lists_others_and_refuses_staff_and_in_world()
    {
        await using var rig = Factory();
        var (first, _) = await TakeSwordAsync(rig);
        var bobRoot = (await rig.F.Inventory.FoldersAsync(BobId, Ct)).Single(f => f.Type == 8).Id;
        rig.F.Inventory.Add(BobId, bobRoot, "Bought sword", Asset, 6);
        var admin = TestAuth.Account(StaffRole.Admin).PrincipalId;
        rig.F.Inventory.AddDefaultFolders(admin);
        var adminRoot = (await rig.F.Inventory.FoldersAsync(admin, Ct)).Single(f => f.Type == 8).Id;
        rig.F.Inventory.Add(admin, adminRoot, "Admin's sword", Asset, 6);

        var rows = (await rig.Actions.FindCopiesAsync(As(StaffRole.Owner), null, first.Number!, Ct))!;

        Assert.Equal(2, rows.Count);
        Assert.Null(rows.Single(r => r.Holding.Owner == BobId).Refusal);
        Assert.Equal(PeopleRules.StaffFirst, rows.Single(r => r.Holding.Owner == admin).Refusal);
        Assert.Contains(rig.Store.Get(first.Number!)!.Events, e => e.What == "copies-searched");

        rig.F.People.Online[BobId] = RegionId;
        rows = (await rig.Actions.FindCopiesAsync(As(StaffRole.Owner), null, first.Number!, Ct))!;
        Assert.Equal(PeopleActions.InWorld, rows.Single(r => r.Holding.Owner == BobId).Refusal);
    }

    [Fact]
    public async Task Taking_copies_adds_copy_holders()
    {
        await using var rig = Factory();
        var (first, _) = await TakeSwordAsync(rig);
        var bobRoot = (await rig.F.Inventory.FoldersAsync(BobId, Ct)).Single(f => f.Type == 8).Id;
        var bought = rig.F.Inventory.Add(BobId, bobRoot, "Bought sword", Asset, 6);

        var outcome = await rig.Actions.TakeCopiesAsync(As(StaffRole.Admin), null, first.Number!, [(BobId, bought.ItemId)], Ct);

        Assert.True(outcome.Done, outcome.Message);
        var c = rig.Store.Get(first.Number!)!;
        Assert.Equal(PersonOnCase.CopyHolder, c.People.Single(p => p.Principal == BobId).Kind);
        Assert.Equal(2, c.Items.Count);
        Assert.DoesNotContain(rig.F.Inventory.Items, i => i.ItemId == bought.ItemId);
        Assert.True(rig.F.Services.GetRequiredService<AccountHolds>().IsHeld(BobId));
    }

    [Fact]
    public async Task Put_back_restores_and_records_why()
    {
        await using var rig = Factory();
        var (first, sword) = await TakeSwordAsync(rig);

        var outcome = await rig.Actions.PutBackAsync(As(StaffRole.Admin), null, first.Number!, sword.ItemId, "Counter-notice received", Ct);

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal("Put back Sword in My Inventory.", outcome.Message);
        Assert.Contains(rig.F.Inventory.Items, i => i.ItemId == sword.ItemId);
        var c = rig.Store.Get(first.Number!)!;
        Assert.NotNull(c.Items[0].PutBackAt);
        Assert.Contains(c.Events, e => e.What == "put-back" && e.Why == "Counter-notice received");
        Assert.Single(Audit(rig.F, "takedown-restored"));
        var sent = await SentAsync(rig, 2);
        Assert.Contains(("ann@example.com", "Items put back in your Avalon inventory"), sent);

        var twice = await rig.Actions.PutBackAsync(As(StaffRole.Admin), null, first.Number!, sword.ItemId, "Again", Ct);
        Assert.False(twice.Done);
    }

    [Fact]
    public async Task Put_back_needs_a_why()
    {
        await using var rig = Factory();
        var (first, sword) = await TakeSwordAsync(rig);
        var outcome = await rig.Actions.PutBackAsync(As(StaffRole.Admin), null, first.Number!, sword.ItemId, "  ", Ct);
        Assert.False(outcome.Done);
        Assert.Equal(TakedownActions.WhyNeeded, outcome.Message);
        Assert.DoesNotContain(rig.F.Inventory.Items, i => i.ItemId == sword.ItemId);
    }

    [Fact]
    public async Task Close_needs_an_outcome_and_makes_the_case_read_only()
    {
        await using var rig = Factory();
        var (first, sword) = await TakeSwordAsync(rig);
        var admin = As(StaffRole.Admin);

        Assert.Equal(TakedownActions.WhyNeeded, rig.Actions.Close(admin, null, first.Number!, "").Message);
        Assert.True(rig.Actions.AddNote(admin, null, first.Number!, "Called the sender.").Done);
        var closed = rig.Actions.Close(admin, null, first.Number!, "removed for good");

        Assert.Equal($"Case {first.Number} is closed.", closed.Message);
        var c = rig.Store.Get(first.Number!)!;
        Assert.False(c.Open);
        Assert.Equal("removed for good", c.Outcome);
        Assert.Equal(TakedownActions.Closed, rig.Actions.AddNote(admin, null, first.Number!, "More").Message);
        Assert.Equal(TakedownActions.Closed, (await rig.Actions.PutBackAsync(admin, null, first.Number!, sword.ItemId, "why", Ct)).Message);
        Assert.False(rig.F.Services.GetRequiredService<AccountHolds>().IsHeld(AnnId));
    }

    [Fact]
    public async Task Audit_targets_never_contain_the_notice_text()
    {
        await using var rig = Factory();
        var (first, _) = await TakeSwordAsync(rig);
        rig.Actions.AddNote(As(StaffRole.Admin), null, first.Number!, "a private note");
        rig.Actions.Close(As(StaffRole.Admin), null, first.Number!, "a private outcome");
        var all = string.Join("\n", Audit(rig.F).Select(e => e.ToString()));
        Assert.DoesNotContain("secret sword text", all);
        Assert.DoesNotContain("Rita", all);
        Assert.DoesNotContain("private", all);
        Assert.Single(Audit(rig.F, "takedown-note"));
        Assert.Single(Audit(rig.F, "takedown-closed"));
    }

    [Fact]
    public async Task A_step_during_find_copies_is_not_lost()
    {
        await using var rig = Factory();
        var (first, _) = await TakeSwordAsync(rig);
        rig.F.Inventory.DuringCopies = () =>
        {
            rig.Actions.AddNote(As(StaffRole.Admin), null, first.Number!, "Written meanwhile");
            return Task.CompletedTask;
        };

        await rig.Actions.FindCopiesAsync(As(StaffRole.Admin), null, first.Number!, Ct);

        var c = rig.Store.Get(first.Number!)!;
        Assert.Contains(c.Events, e => e.What == "note" && e.Why == "Written meanwhile");
        Assert.Contains(c.Events, e => e.What == "copies-searched");
    }

    [Fact]
    public async Task Removal_lost_after_the_commit_keeps_the_items_on_the_case()
    {
        await using var rig = Factory();
        rig.F.Inventory.FailAfterRemove = true;

        var (outcome, sword) = await TakeSwordAsync(rig);

        Assert.DoesNotContain(rig.F.Inventory.Items, i => i.ItemId == sword.ItemId);
        var c = Assert.Single(rig.Store.All());
        Assert.Equal(sword, Assert.Single(c.Items).Row);
        Assert.True(outcome.Done, outcome.Message);
    }

    [Fact]
    public async Task A_cancelled_request_still_finishes_the_removal()
    {
        await using var rig = Factory();
        var sword = rig.F.Inventory.Add(AnnId, rig.AnnRoot, "Sword", Asset, 6);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await rig.Actions.TakeDownAsync(As(StaffRole.Admin), null, AnnId, [sword.ItemId], null, Notice(), false, cts.Token);

        Assert.True(outcome.Done, outcome.Message);
        Assert.DoesNotContain(rig.F.Inventory.Items, i => i.ItemId == sword.ItemId);
        Assert.Equal(sword, Assert.Single(Assert.Single(rig.Store.All()).Items).Row);
    }

    [Fact]
    public async Task Put_back_that_worked_is_recorded_even_when_the_folder_name_cannot_be_read()
    {
        await using var rig = Factory();
        var (first, sword) = await TakeSwordAsync(rig);
        rig.F.Inventory.FailFoldersAfterPutBack = true;

        var outcome = await rig.Actions.PutBackAsync(As(StaffRole.Admin), null, first.Number!, sword.ItemId, "Counter-notice", Ct);

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal("Put back Sword in their inventory.", outcome.Message);
        Assert.NotNull(rig.Store.Get(first.Number!)!.Items[0].PutBackAt);
    }

    [Fact]
    public async Task Find_copies_with_the_database_down_says_so_and_records_nothing()
    {
        await using var rig = Factory();
        var (first, _) = await TakeSwordAsync(rig);
        rig.F.Inventory.Down = true;

        var rows = await rig.Actions.FindCopiesAsync(As(StaffRole.Admin), null, first.Number!, Ct);

        Assert.Null(rows);
        Assert.DoesNotContain(rig.Store.Get(first.Number!)!.Events, e => e.What == "copies-searched");
    }
}
