# Duplicate-code clean-up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each duplicated block in the review's items 1–10 and the `[?]` item has one shared helper and the copies call it, with the launcher behaving the same apart from the one decided `AccountChanges` change.

**Architecture:** Small static helpers (or one abstract page base, one generic cookie class, three tag helpers) placed next to the code they serve, each introduced in its own task with its own commit. Where a helper holds logic it gets a unit test first; where code only moves, pinning tests written before the move must stay green after it. Copies that differ on purpose are left alone and listed for the PR body.

**Tech Stack:** .NET 10, C#, ASP.NET Core Razor Pages, MySqlConnector, xUnit v3 on Microsoft.Testing.Platform (`global.json` selects it), AngleSharp for page tests.

**Spec:** `docs/superpowers/specs/2026-11-13-duplicate-code-cleanup-design.md` (the review's item list with file references is the section "Code clean-up — duplicate code (review 2026-11-12)" in `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`, lines 253–292).

## Global Constraints

- .NET 10, C#, the project's existing style (file-scoped namespaces, `sealed` classes, small static helpers).
- No new features, no page or wording changes a resident or staff member would see.
- No changes to upstream OpenSim code or `bin/` templates. GridGate's own tools under `OpenSim/Tools/GridGate.*` are GridGate code and in scope (Task 15).
- No renames of public routes, settings keys, cookie names or file formats.
- Text in logs and audit entries stays word for word (the launcher's log file holds the formatted message, so a literal prefix may become a `{Area}` template argument as long as the rendered line is the same).
- Not a clean-up that changes what a page shows, what the audit log records, what gets logged at warning level, or which error a resident sees.
- No helper that hides control flow (no generic "run anything with retries" wrapper); helpers stay small and named for what they do.
- The one behaviour change: `Guests/AccountChanges.cs` moves to the common Robust rule.
- Every task ends with its scoped tests green; the full unit suite (about 15 minutes, 3,957 passed / 10 skipped on 2026-11-13) runs in Task 16. Never loosen a test to get it green.
- The CI guards (`check-retired-names.sh`, `check-old-name.sh`, secret scan) stay green.
- `TreatWarningsAsErrors` is on in `GridGate.Launcher` and `GridGate.Launcher.Core`: an `async` lambda with no `await` (CS1998) fails the build, so wrap synchronous bodies with `Task.FromResult` / `Task.CompletedTask` instead.
- Work happens in `C:\Users\ystud\Desktop\CODE\GridGate-cleanup` on `chore/duplicate-code-cleanup`. Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- A new duplicate found on the way is not fixed; add it to the "PR notes" list in Task 17.

## Review Focus

1. A resident's request already given up (browser closed) while Robust is down, on the account page: the failure now goes through instead of an answer of "busy". Test: Task 1, `Robust_down_after_the_request_was_given_up_is_not_answered_busy`.
2. An error or notice text with `<`, `&` or an empty string: still HTML-encoded, an empty string still draws the (empty) box, only null draws nothing. Test: Task 4, `StatusBoxesTests`.
3. A two-factor sign-in started just before the upgrade (cookie issued by the old code, inside its five minutes): the code step still reads it. Test: Task 9, the two `..._from_before_the_change_is_still_read` tests.
4. A grid operation that throws inside the operation lock: the lock is free for the next operation and the dashboard activity is back to none. Test: Task 8, `A_failed_operation_frees_the_lock_and_clears_the_activity`.
5. A mesh with no vertices: the shared bounds give zero vectors and "none", so the unit box stays 0 and the simplifier's diagonal stays 0. Test: Task 15, `Bounds_of_no_vertices_is_zero_and_none`.

---

### Task 1: One rule for "Robust didn't answer" (`[?]` item)

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/OpenSim/RobustFailure.cs`
- Modify: `Launcher/GridGate.Launcher/Estates/AvatarLookup.cs:23-24`
- Modify: `Launcher/GridGate.Launcher/Guests/AccountChanges.cs:172, 236, 272-274` (delete `Unreachable`)
- Modify: `Launcher/GridGate.Launcher/Guests/PasswordResets.cs:89-90, 171-172`
- Modify: `Launcher/GridGate.Launcher/Guests/SignUpApprovals.cs:163-165` (`IsDown` body)
- Modify: `Launcher/GridGate.Launcher/Guests/SignUpCleanup.cs:110-112` (`IsDown` body)
- Modify: `Launcher/GridGate.Launcher/Guests/SignUps.cs:332-334` (`IsDown` body)
- Modify: `Launcher/GridGate.Launcher/Guests/SignUpNotices.cs:45-46`, `Launcher/GridGate.Launcher/Guests/TicketSweep.cs:108-109`
- Modify: `Launcher/GridGate.Launcher/Security/PeopleActions.cs:99-100`
- Modify pages: `Pages/Groups/Group.cshtml.cs:56-57`, `Pages/Guest/Account.cshtml.cs:108-109`, `Pages/Guest/SignIn.cshtml.cs:77-78`, `Pages/Login.cshtml.cs:67-68`, `Pages/People/Index.cshtml.cs:193-194`, `Pages/Regions/Region.cshtml.cs:228-229`, `Pages/Staff/Index.cshtml.cs:56-57` (all under `Launcher/GridGate.Launcher/`)
- Test: `Launcher/GridGate.Launcher.Tests/RobustFailureTests.cs` (new), `Launcher/GridGate.Launcher.Tests/AccountChangesTests.cs`

**Interfaces:**
- Produces: `GridGate.Launcher.Core.OpenSim.RobustFailure.IsUnreachable(Exception e, CancellationToken ct) : bool` — `public static`; true when `e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or InvalidDataException` and `!ct.IsCancellationRequested`.

- [ ] **Step 1: Write the failing tests**

`RobustFailureTests` (new, `public sealed class`, namespace `GridGate.Launcher.Tests`):

```csharp
[Theory]
[InlineData(typeof(HttpRequestException))]
[InlineData(typeof(TaskCanceledException))]
[InlineData(typeof(IOException))]
[InlineData(typeof(InvalidOperationException))]
[InlineData(typeof(InvalidDataException))]
[InlineData(typeof(FileNotFoundException))] // a subclass counts, as `is` does today
public void The_five_kinds_are_unreachable_while_the_caller_waits(Type kind) =>
    Assert.True(RobustFailure.IsUnreachable((Exception)Activator.CreateInstance(kind)!, CancellationToken.None));

// Same six InlineData rows:
public void Nothing_is_unreachable_once_the_caller_gave_up(Type kind) =>
    Assert.False(RobustFailure.IsUnreachable((Exception)Activator.CreateInstance(kind)!, new CancellationToken(canceled: true)));

[Theory]
[InlineData(typeof(OperationCanceledException))]
[InlineData(typeof(TimeoutException))]
[InlineData(typeof(ArgumentException))]
public void Other_failures_are_not_unreachable(Type kind) => Assert.False(RobustFailure.IsUnreachable(..., CancellationToken.None));

[Fact]
public void The_staff_list_is_not_part_of_the_rule() =>
    Assert.False(RobustFailure.IsUnreachable(new StaffDirectoryUnavailableException("x"), CancellationToken.None));
```

`AccountChangesTests` (add under "Change password (§5)", next to `Robust_down_is_busy_not_counted`):

```csharp
[Fact]
public async Task Robust_down_after_the_request_was_given_up_is_not_answered_busy()
{
    await using var rig = Factory();
    rig.F.Robust.Down = true;
    await Assert.ThrowsAsync<HttpRequestException>(() =>
        rig.Changes.ChangePasswordAsync(AnnId, Old, null, New, New, Caller, new CancellationToken(canceled: true), "en"));
    Assert.False(rig.Throttle.IsLocked(SignInDoor.Guest, Key, Caller));
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*RobustFailureTests" --filter-class "*AccountChangesTests"`
Expected: build error CS0103 "The name 'RobustFailure' does not exist" (comment out `RobustFailureTests` to see the second failure: `Robust_down_after_the_request_was_given_up_is_not_answered_busy` fails with "No exception was thrown", because `Unreachable` answers Busy).

- [ ] **Step 3: Add `RobustFailure.IsUnreachable` in `Launcher/GridGate.Launcher.Core/OpenSim/RobustFailure.cs`**

A one-line `public static class`; the summary says it is the "Robust didn't answer" rule and that callers add `StaffDirectoryUnavailableException` themselves.

- [ ] **Step 4: Move every copy to the rule**

Every `when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or InvalidDataException && !ct.IsCancellationRequested)` (and the `Guest/Account` copy, which lists the same five in another order) becomes `when (RobustFailure.IsUnreachable(e, ct))`; the log line and the body stay as they are. The staff-list sites keep their own cancel behaviour for `StaffDirectoryUnavailableException`:

| Site | New filter / body |
|---|---|
| `SignUpApprovals.IsDown(Exception e)` (no token today, call sites unchanged) | `e is StaffDirectoryUnavailableException \|\| RobustFailure.IsUnreachable(e, CancellationToken.None)` |
| `SignUps.IsDown(e, ct)`, `SignUpCleanup.IsDown(e, ct)` (staff list gated by `ct` today) | `!ct.IsCancellationRequested && (e is StaffDirectoryUnavailableException \|\| RobustFailure.IsUnreachable(e, ct))` |
| `SignUpNotices.cs:45`, `TicketSweep.cs:108` (gated today) | same expression as SignUps, inline in the `when` |
| `AccountChanges.cs:172` (staff list not gated today) | `RobustFailure.IsUnreachable(e, ct) \|\| e is StaffDirectoryUnavailableException` |
| `AccountChanges.cs:236` | `RobustFailure.IsUnreachable(e, ct)`; delete the private `Unreachable` (272-274) |

Leave on purpose (different set, or not the account service): `Pages/Advanced/Join.cshtml.cs:86`, `Pages/Support/SupportGuest.cs:19`, `Endpoints/RegionsApi.cs:47`, `Endpoints/SplashApi.cs:179`, `Pages/Security/Email.cshtml.cs:235` and `EmailWording.cshtml.cs:190` (`OwnerEmailAsync`: it *is* a Robust lookup, but moving it would be a second behaviour change; see PR notes), `Dashboard/ServiceChecks.cs:105`, `Estates/EstatesAdmin.cs:181`, `Security/PeopleActions.cs:399`, `Security/StaffActions.cs:137`, everything in `Launcher/GridGate.Launcher.Core/Grid`, `Hypergrid`, `Plans`, `Config`, `Api/LegacyCommandApi.cs:119` and `OpenSim/RestConsoleClient.cs`, `RobustAssets.cs`.

- [ ] **Step 5: Check no copy is left**

Run: `grep -rn "or InvalidDataException" --include=*.cs Launcher/GridGate.Launcher Launcher/GridGate.Launcher.Core`
Expected: only `Launcher/GridGate.Launcher.Core/OpenSim/RobustFailure.cs` and `Launcher/GridGate.Launcher.Core/Iar/IarArchive.cs:70` (archive reading, not Robust).

- [ ] **Step 6: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*RobustFailureTests" --filter-class "*AccountChangesTests" --filter-class "*SignUpsTests" --filter-class "*SignUpApprovalsTests" --filter-class "*SignUpCleanupTests" --filter-class "*SignUpNoticesTests" --filter-class "*TicketSweepTests" --filter-class "*PasswordResetTests" --filter-class "*LoginTests" --filter-class "*GuestSignInTests" --filter-class "*AccountPageTests" --filter-class "*PeoplePagesTests" --filter-class "*PeopleActionsTests" --filter-class "*StaffPagesTests" --filter-class "*GroupsPagesTests" --filter-class "*EstatesPagesTests" --filter-class "*RegionOwnerCardTests"`
Expected: `Test run summary: Passed!`, failed 0.

- [ ] **Step 7: Commit**

```bash
git add Launcher/GridGate.Launcher.Core/OpenSim/RobustFailure.cs Launcher/GridGate.Launcher Launcher/GridGate.Launcher.Tests/RobustFailureTests.cs Launcher/GridGate.Launcher.Tests/AccountChangesTests.cs
git commit -F - <<'EOF'
refactor: one "Robust didn't answer" rule (RobustFailure.IsUnreachable)

The 17 copies agreed on the five exception types and differed only in where the
cancel check sat. The staff-list sites keep their own cancel behaviour for
StaffDirectoryUnavailableException. AccountChanges moves to the common rule: a
request already given up now ends with its failure instead of "busy".

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 2: One way to open a grid database connection (item 1, first half)

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Database/GridDb.cs`
- Modify (the open lines only): `Dashboard/WorldCounter.cs:104-105`, `Database/AccountRemover.cs:87-88, 116-117`, `Database/AppearanceStore.cs:32-33`, `Database/DatabaseCheck.cs:16-18`, `Database/EstateRemover.cs:41-42`, `Database/InventoryStore.cs:291-292`, `Database/PeopleDirectory.cs:109-110`, `Database/StaffDirectory.cs:61-62`, `Estates/GridEstates.cs:178-179, 198-199`, `Grid/GridRegions.cs:43-44`, `Grid/OnlineUsers.cs:45-46`, `Groups/GridGroups.cs:223-224`, `Hypergrid/GridHyperlinks.cs:112-113`, `OpenSim/RobustEmails.cs:34-35`, `OpenSim/RobustNames.cs:42-43`, `OpenSim/RobustPasswords.cs:50-51`, `Database/RegionStore.cs:126-136` (all under `Launcher/GridGate.Launcher.Core/`)
- Test: `Launcher/GridGate.Launcher.Tests/GridDbTests.cs` (new; `GridDb` is internal, `InternalsVisibleTo` already covers the test project)

**Interfaces:**
- Produces (`internal static class GridGate.Launcher.Core.Database.GridDb`):
  - `Task<MySqlConnection> OpenAsync(string connectionString, CancellationToken ct)` — opens; disposes the connection and rethrows when the open fails (the body of today's `RegionStore.OpenAsync`).
  - `Task<MySqlConnection> OpenRobustAsync(LauncherSettings s, uint timeoutSeconds, CancellationToken ct)` — `MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, timeoutSeconds)`.
  - `Task<MySqlConnection> OpenOpensimAsync(LauncherSettings s, uint timeoutSeconds, CancellationToken ct)` — `"opensim", "opensimuser", s.Db.OpensimPassword`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Opening_a_closed_port_throws_the_drivers_error()
{
    var s = TestSettings.Create() with { MariaDbPort = TestPorts.Free(1)[0] };
    await Assert.ThrowsAsync<MySqlException>(() => GridDb.OpenRobustAsync(s, 2, TestContext.Current.CancellationToken));
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*GridDbTests"`
Expected: build error CS0103 "The name 'GridDb' does not exist".

- [ ] **Step 3: Implement the three methods in `GridDb.cs`**

- [ ] **Step 4: Move the 18 sites and `RegionStore`**

Each `var s = settings(); await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s, "robust", …, TimeoutSeconds)); await conn.OpenAsync(ct);` becomes `var s = settings(); await using var conn = await GridDb.OpenRobustAsync(s, TimeoutSeconds, ct);` (keep `var s` where the method uses `s` again). Keep each class's own `TimeoutSeconds` (3, 4, 5 or 10). Keep the token each site passes today: the four `RunAsync`-style wrappers (`GridEstates.cs:198`, `GridGroups.cs:223`, `GridHyperlinks.cs:112`, `PeopleDirectory.cs:109`) open with no token, so they pass `CancellationToken.None`; `InventoryStore.cs:292` keeps `ct`. `WorldCounter` (its own database and user) uses `GridDb.OpenAsync(MariaDbServer.ConnectionString(s, database, user, password, TimeoutSeconds), ct)`. `RegionStore.OpenAsync` becomes `GridDb.OpenAsync(MariaDbServer.ConnectionString(s), ct)` (delete the private method, call `GridDb` at its two call sites).

Leave: the root connections in `MariaDbServer.cs:168, 181, 279, 292` and `DatabaseProvisioner.cs:11` (the server's own start-up and set-up, not one of the review's 18).

Check: `grep -rn "new MySqlConnection(" --include=*.cs Launcher/GridGate.Launcher.Core` — Expected: only `Database/GridDb.cs`, the four in `Database/MariaDbServer.cs` and `Database/DatabaseProvisioner.cs:11`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*GridDbTests" --filter-class "*InventoryStoreTests" --filter-class "*PeopleDirectoryTests" --filter-class "*DashboardTests"`
Expected: Passed, failed 0.
When `GRIDGATE_MARIADB_BIN` points at a MariaDB 11.4 `bin` folder, also run: `dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release --filter-class "*AccountRemoverIntegrationTests" --filter-class "*EstatesHyperlinksIntegrationTests" --filter-class "*GridGroupsTests" --filter-class "*GridRegionsTests" --filter-class "*InventoryStoreIntegrationTests" --filter-class "*OnlineUsersTests" --filter-class "*PeopleDirectoryIntegrationTests" --filter-class "*RegionStoreTests" --filter-class "*RobustNamesIntegrationTests"` — Expected: Passed, failed 0, none skipped. Without MariaDB they skip; CI's launcher jobs run them.

- [ ] **Step 6: Commit**

```bash
git add Launcher/GridGate.Launcher.Core Launcher/GridGate.Launcher.Tests/GridDbTests.cs
git commit -F - <<'EOF'
refactor: open grid database connections through GridDb

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 3: One `RunAsync`, one `Command`/`ExecAsync`/`ScalarAsync`, one id parse (item 1, second half)

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Database/GridDb.cs`
- Modify: `Estates/GridEstates.cs:193-226` (`RunAsync` body, delete `Command`, `ExecAsync`, `Id`), `Groups/GridGroups.cs:218-256` (`RunAsync` body, delete `Command`, `ExecAsync`, `ScalarAsync`), `Hypergrid/GridHyperlinks.cs:107-120` (`RunAsync` body), `Database/PeopleDirectory.cs:104-125` (`WithConnectionAsync` body, delete `Command`), `Database/InventoryStore.cs:261-266` (delete `Exec`), `Database/RegionStore.cs:151-156` (delete `RunAsync`), `Grid/GridRegions.cs:51`, `Grid/OnlineUsers.cs:53-54` (all under `Launcher/GridGate.Launcher.Core/`)
- Test: `Launcher/GridGate.Launcher.Tests/GridDbTests.cs`

**Interfaces:**
- Consumes: `GridDb.OpenRobustAsync`, `GridDb.OpenOpensimAsync` (Task 2); `InventoryStore.IdOf(object? value) : Guid` (exists, public).
- Produces (on `GridDb`):
  - `Task<T> RunAsync<T>(Func<Task<MySqlConnection>> open, Func<MySqlConnection, Task<T>> work, Func<Exception, Exception> unavailable)` — opens, runs `work`, disposes; an exception that `is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException or InvalidCastException or FormatException or OverflowException` is rethrown as `unavailable(e)`; anything else passes through.
  - `MySqlCommand Command(MySqlConnection conn, MySqlTransaction? tx, string sql, params (string Name, object? Value)[] ps)`
  - `Task<int> ExecAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)`
  - `Task<long> ScalarAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)` — `Convert.ToInt64(await cmd.ExecuteScalarAsync(ct))`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_database_failure_becomes_the_callers_exception()
{
    var e = await Assert.ThrowsAsync<GroupsUnavailableException>(() => GridDb.RunAsync<int>(
        () => throw new TimeoutException("slow"), _ => Task.FromResult(1), x => new GroupsUnavailableException("The database could not be read.", x)));
    Assert.IsType<TimeoutException>(e.InnerException);
}

[Fact]
public async Task Any_other_failure_passes_through() =>
    await Assert.ThrowsAsync<ArgumentException>(() => GridDb.RunAsync<int>(
        () => throw new ArgumentException("no"), _ => Task.FromResult(1), x => new GroupsUnavailableException("x", x)));
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*GridDbTests"`
Expected: build error CS0117 "'GridDb' does not contain a definition for 'RunAsync'".

- [ ] **Step 3: Implement `RunAsync`, `Command`, `ExecAsync`, `ScalarAsync` in `GridDb.cs`**

- [ ] **Step 4: Point the copies at them**

The four private wrappers keep their names (their many call sites stay) and become one-liners:
- `GridEstates.RunAsync`: `GridDb.RunAsync(() => GridDb.OpenOpensimAsync(settings(), TimeoutSeconds, CancellationToken.None), work, e => new EstatesUnavailableException("The regions' database could not be read.", e))`
- `GridGroups.RunAsync`: robust; `unavailable` keeps the duplicate-key case first: `e => e is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry } ? new GroupsUnavailableException("The database refused the change.", e) : new GroupsUnavailableException("The database could not be read.", e)`
- `GridHyperlinks.RunAsync`: robust, `new HyperlinksUnavailableException("The database could not be read.", e)`
- `PeopleDirectory.WithConnectionAsync`: robust, `new PeopleDirectoryUnavailableException("The database could not be read.", e)`

`Command`/`ExecAsync`/`ScalarAsync` calls in GridEstates and GridGroups go to `GridDb.*`; `PeopleDirectory.ReadAsync` uses `GridDb.Command(conn, null, sql, [.. p])`; `InventoryStore.Exec(...)` calls become `await GridDb.ExecAsync(...)` (result ignored); `RegionStore.RunAsync(conn, tx, sql, id, ct)` calls become `GridDb.ExecAsync(conn, tx, sql, ct, ("@id", id.ToString()))`. `GridEstates.Id(r, i)` calls become `InventoryStore.IdOf(r.GetValue(i))`; `GridRegions.cs:51` and `OnlineUsers.cs:53-54` use `InventoryStore.IdOf(reader.GetValue(n))`.

Leave: `InventoryStore.Run` (286-299) keeps its own body and narrower filter (no `OverflowException`, lets `NoInventoryException` through, `ct` on the open) and only uses `GridDb.OpenRobustAsync(s, TimeoutSeconds, ct)`; `GridGroups.Text` and `GridHyperlinks.Text` (they return text, also for non-id columns); `StaffDirectory.cs:72` (skips a row whose id doesn't parse, where `IdOf` would give `Guid.Empty`); `PeopleDirectory.AsGuid` (returns `Guid?`); `GridEstates.NamesAsync` (178-189) keeps swallowing its errors (where GridGroups' lookup throws).

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*GridDbTests" --filter-class "*InventoryStoreTests" --filter-class "*PeopleDirectoryTests" --filter-class "*GroupsAdminTests" --filter-class "*EstatesPagesTests" --filter-class "*HypergridPagesTests"`
Expected: Passed, failed 0. With MariaDB available, the integration command from Task 2 Step 5: Passed, none skipped.

- [ ] **Step 6: Commit**

```bash
git add Launcher/GridGate.Launcher.Core Launcher/GridGate.Launcher.Tests/GridDbTests.cs
git commit -F - <<'EOF'
refactor: one RunAsync and one Command/Exec/Scalar for the grid database

Each wrapper keeps its own exception and message; InventoryStore keeps its
narrower filter.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 4: Error and notice boxes as tag helpers (item 2)

**Files:**
- Create: `Launcher/GridGate.Launcher/Navigation/StatusBoxes.cs` (beside `ButtonIcons.cs`; `@addTagHelper *, GridGate.Launcher` in `Pages/_ViewImports.cshtml` already registers it)
- Modify: the `.cshtml` files under `Launcher/GridGate.Launcher/Pages/` that match the rule in Step 6 (about 40 error boxes, 25 ok boxes, 7 notice paragraphs)
- Test: `Launcher/GridGate.Launcher.Tests/StatusBoxesTests.cs` (new), `Launcher/GridGate.Launcher.Tests/PeoplePagesTests.cs`, `Launcher/GridGate.Launcher.Tests/RegionPanelTests.cs`

**Interfaces:**
- Produces (namespace `GridGate.Launcher.Navigation`):
  - `public abstract class StatusBoxTagHelper(string tag, string classes, string role) : TagHelper` with `[HtmlAttributeName("text")] public string? Text { get; set; }`; `Process`: null `Text` → `output.SuppressOutput()`; otherwise `TagName = tag`, `TagMode = StartTagAndEndTag`, `class` then `role` attributes (in that order), `Content.SetContent(Text)` (encoded).
  - `[HtmlTargetElement("error-box", TagStructure = TagStructure.WithoutEndTag)] public sealed class ErrorBoxTagHelper() : StatusBoxTagHelper("div", "error-box error alert alert-danger", "alert")`
  - `[HtmlTargetElement("ok-box", …)] public sealed class OkBoxTagHelper() : StatusBoxTagHelper("div", "ok-box alert alert-success", "status")`
  - `[HtmlTargetElement("notice-box", …)] public sealed class NoticeBoxTagHelper() : StatusBoxTagHelper("p", "notice alert alert-info py-2", "status")`

- [ ] **Step 1: Write the failing unit tests**

`StatusBoxesTests` renders a helper by hand: a `TagHelperContext(tag, [], new Dictionary<object, object>(), "t")`, a `TagHelperOutput(tag, [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent())) { TagMode = TagMode.SelfClosing }`, `Process`, then `WriteTo(StringWriter, HtmlEncoder.Default)`.

```csharp
Assert.Equal("<div class=\"error-box error alert alert-danger\" role=\"alert\">No avatar called Ann &lt;b&gt; &amp; co.</div>",
    Render(new ErrorBoxTagHelper { Text = "No avatar called Ann <b> & co." }, "error-box"));        // Error_box_is_the_danger_div
Assert.Equal("<div class=\"ok-box alert alert-success\" role=\"status\">Saved.</div>",
    Render(new OkBoxTagHelper { Text = "Saved." }, "ok-box"));                                       // Ok_box_is_the_success_div
Assert.Equal("<p class=\"notice alert alert-info py-2\" role=\"status\">Moving Home.</p>",
    Render(new NoticeBoxTagHelper { Text = "Moving Home." }, "notice-box"));                         // Notice_box_is_the_info_paragraph
Assert.Equal("", Render(new ErrorBoxTagHelper { Text = null }, "error-box"));                       // Null_text_writes_nothing
Assert.Equal("<div class=\"ok-box alert alert-success\" role=\"status\"></div>",
    Render(new OkBoxTagHelper { Text = "" }, "ok-box"));                                             // Empty_text_still_writes_the_box
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*StatusBoxesTests"`
Expected: build error CS0246 "The type or namespace name 'ErrorBoxTagHelper' could not be found".

- [ ] **Step 3: Implement `StatusBoxes.cs`**

- [ ] **Step 4: Run them to see them pass**

Same command. Expected: Passed, 5 tests.

- [ ] **Step 5: Write the page pinning tests and see them pass on the old markup**

These are written before any view changes and must pass both before and after Step 6.

`PeoplePagesTests.Status_box_markup_is_unchanged` (reuse the flows of `Reset_post_sends_and_returns_to_the_same_page` and `Delete_without_confirm_is_refused`):

```csharp
Assert.Equal("<div class=\"ok-box alert alert-success\" role=\"status\">A reset link was sent to Gail Guest.</div>",
    page.QuerySelector(".ok-box")!.OuterHtml);
var error = refused.QuerySelector(".error-box")!;
Assert.Equal("DIV", error.TagName);
Assert.Equal("error-box error alert alert-danger", error.GetAttribute("class"));
Assert.Equal("alert", error.GetAttribute("role"));
Assert.Equal(0, error.ChildElementCount);
```

`RegionPanelTests.Notice_markup_is_unchanged` (the flow of `Power_is_refused_while_a_job_runs_in_its_program`):

```csharp
Assert.Equal("<p class=\"notice alert alert-info py-2\" role=\"status\">Saving Home as an OAR is still running. Try again when it finishes.</p>",
    panel.QuerySelector(".notice")!.OuterHtml);
```

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*PeoplePagesTests" --filter-class "*RegionPanelTests"`
Expected: Passed (the old markup already matches).

- [ ] **Step 6: Replace the copies in the views**

Convert a block only when its guard is exactly `@if (<expr> is not null)` with nothing in it but one box, and the box is exactly one of:
- `<div class="error-box error alert alert-danger" role="alert">@<expr></div>` → `<error-box text="@<expr>" />`
- `<div class="ok-box alert alert-success" role="status">@<expr></div>` → `<ok-box text="@<expr>" />`
- `<p class="notice alert alert-info py-2" role="status">@<expr></p>` → `<notice-box text="@<expr>" />`

Keep the tags where the blocks were, so the order on each page stays (Translate shows the notice before the error). Leave, because their markup differs on purpose: the `alert alert-success` notices without `ok-box` (`Advanced/PasswordReset`, `Guest/Account`, `Guest/Connect`), the `alert alert-info` notices on the two `TwoFactor` pages, `People/SignUps`' `ok-box alert alert-info`, the legal-link error and its `else` branch in `Security/Languages`, `People/Person.cshtml:11` and `People/Delete.cshtml:10` (inside a larger block), `Guest/UploadReport.cshtml:32-35` (extra guard conditions), `Regions/Region.cshtml:172` (`<p … py-2>` error), the setup and join pages' `error-box alert alert-danger` boxes (no role, `<p>` inside), and every box whose content is not a single `@<expr>`.

- [ ] **Step 7: Check what is left**

Run: `grep -rnF 'role="alert">@Model.Error</div>' Launcher/GridGate.Launcher/Pages; grep -rnF 'class="ok-box alert alert-success" role="status">@Model.Notice</div>' Launcher/GridGate.Launcher/Pages; grep -rnF 'class="notice alert alert-info py-2"' Launcher/GridGate.Launcher/Pages`
Expected: only `People/Person.cshtml:11`, `Security/Languages.cshtml` (the `else` branch), `People/Delete.cshtml:10` and `Guest/UploadReport.cshtml:34`.

- [ ] **Step 8: Run the page tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*StatusBoxesTests" --filter-class "*PeoplePagesTests" --filter-class "*RegionPanelTests" --filter-class "*IarGuestPagesTests" --filter-class "*PersonIarTests" --filter-class "*PersonUploadRightTests" --filter-class "*RegionOwnerCardTests" --filter-class "*HelpLegalWelcomeKeysTests" --filter-class "*LibraryPageTests" --filter-class "*MoveRegionPageTests" --filter-class "*RunsChangePageTests" --filter-class "*RegionSettingsPageTests" --filter-class "*LandingPageTests" --filter-class "*JoiningPagesKeysTests" --filter-class "*SignUpStepsPageTests" --filter-class "*LayoutTests"`
Expected: Passed, failed 0.

- [ ] **Step 9: Commit**

```bash
git add Launcher/GridGate.Launcher/Navigation/StatusBoxes.cs Launcher/GridGate.Launcher/Pages Launcher/GridGate.Launcher.Tests/StatusBoxesTests.cs Launcher/GridGate.Launcher.Tests/PeoplePagesTests.cs Launcher/GridGate.Launcher.Tests/RegionPanelTests.cs
git commit -F - <<'EOF'
refactor: error, ok and notice boxes as tag helpers

The boxes render the same elements, classes and roles; boxes whose markup
differs on purpose stay as they were.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 5: One audit write for a signed-in person (item 3)

**Files:**
- Modify: `Launcher/GridGate.Launcher/Security/AuditMiddleware.cs:12-20` (add the extension to `AuditActor`), `:50-52` (the middleware's own write)
- Modify private `Write` helpers: `Pages/Security/Bots.cshtml.cs:105-106`, `Email.cshtml.cs:265-266`, `EmailWording.cshtml.cs:198-199`, `Index.cshtml.cs:221-222`, `Languages.cshtml.cs:228-229`, `Legal.cshtml.cs:329-330`, `Translate.cshtml.cs:199-200`, `Wording.cshtml.cs:142-143`, `StarterAvatars.cshtml.cs:147-148`, `Pages/Advanced/PasswordReset.cshtml.cs:86-87`, `Pages/Regions/Region.cshtml.cs:303-305`, `Pages/People/Inventory.cshtml.cs:46-48`, `Pages/Security/Iar.cshtml.cs:56-58`; `Security/PeopleActions.cs:430-431`, `Security/TakedownActions.cs:394-395`, `Security/StaffActions.cs:194-195`, `Security/StaffTickets.cs:175-176`, `Estates/EstatesAdmin.cs:203-204`, `Groups/GroupsAdmin.cs:213-214`, `Hypergrid/HypergridAdmin.cs:147-148`; inline: `Endpoints/SplashApi.cs:101, 109-110, 151-153, 188-190, 203-205, 238-239, 251-252` (all under `Launcher/GridGate.Launcher/`)
- Test: `Launcher/GridGate.Launcher.Tests/AuditLogTests.cs`

**Interfaces:**
- Produces: `public static void Write(this AuditLog audit, ClaimsPrincipal actor, string? ip, string action, string? target, string result = "ok")` on `GridGate.Launcher.Security.AuditActor` — `var (name, id, role) = Of(actor); audit.Write(new AuditEntry(name, id, role, ip, action, target, result));`

- [ ] **Step 1: Write the failing test**

`AuditLogTests.Write_for_a_signed_in_person_takes_name_id_and_role_from_their_claims`:

```csharp
var id = Guid.NewGuid();
var user = new ClaimsPrincipal(new ClaimsIdentity(
    [new(ClaimTypes.Name, "Ann Admin"), new(StaffSignIn.PrincipalClaim, id.ToString()),
     .. StaffSignIn.RoleClaims(StaffRole.Admin).Select(r => new Claim(ClaimTypes.Role, r))], "test"));
using var log = new AuditLog(Path.Combine(_dir, "audit.log"), TimeProvider.System);
log.Write(user, "10.0.0.5", "bots-settings", "max 3");
log.Write(user, null, "email-test", null, "failed");
// first line: actor "Ann Admin", actorId id, role "admin", ip "10.0.0.5", action "bots-settings", target "max 3", result "ok"
// second line: ip null, target null, result "failed"
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*AuditLogTests"`
Expected: build error CS1501 "No overload for method 'Write' takes 4 arguments".

- [ ] **Step 3: Add the extension to `AuditActor`**

- [ ] **Step 4: Point the copies at it**

Private `Write` helpers keep their names and call sites and become one line, e.g. `private void Write(string action, string target, string result) => audit.Write(User, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result);`. Keep what differs: EstatesAdmin passes `LogText.Clean(target, 300)`; StarterAvatars passes `LogText.Clean(target, 140)`; GroupsAdmin's target stays nullable; callers that always write "ok" use the default; Security/Iar keeps its loop; SplashApi's publish keeps `AuditActor.Of(ctx.User).Name` for `store.Publish(name)`; the middleware passes its status as `result`.

Leave: `Guests/AccountChanges.cs:279-280`, `Pages/Guest/TwoFactor.cshtml.cs`, `Pages/Login.cshtml.cs`, `Pages/Guest/SignInCode.cshtml.cs`, `Pages/Login/Code.cshtml.cs`, `Guests/SignUps.cs`, `SignUpApprovals.cs`, `SignUpCleanup.cs` (the actor is an account or a resident, not the signed-in staff claims); the `IarStaffActor` builders in `Endpoints/IarStaffApi.cs:38`, `Pages/People/Iar.cshtml.cs:129` and `Pages/Security/StarterAvatars.Import.cs:70, 81` (not audit writes).

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*AuditLogTests" --filter-class "*BotsPageTests" --filter-class "*EmailPageTests" --filter-class "*EmailWordingPageTests" --filter-class "*SecurityPageTests" --filter-class "*LanguagesPageTests" --filter-class "*LegalEditorTests" --filter-class "*WordingPageTests" --filter-class "*TranslationsTests" --filter-class "*StarterAvatarsPageTests" --filter-class "*RecoveryPagesTests" --filter-class "*RegionOwnerCardTests" --filter-class "*PeoplePagesTests" --filter-class "*IarSettingsPageTests" --filter-class "*PeopleActionsTests" --filter-class "*TakedownActionsTests" --filter-class "*StaffPagesTests" --filter-class "*StaffTicketsPagesTests" --filter-class "*EstatesPagesTests" --filter-class "*GroupsAdminTests" --filter-class "*HypergridPagesTests" --filter-class "*SplashApiTests" --filter-class "*SplashPictureApiTests" --filter-class "*ThemeLogoTests" --filter-class "*StaffAccessTests"`
Expected: Passed, failed 0.

- [ ] **Step 6: Commit**

```bash
git add Launcher/GridGate.Launcher Launcher/GridGate.Launcher.Tests/AuditLogTests.cs
git commit -F - <<'EOF'
refactor: one audit write for a signed-in staff member

Entries stay field for field the same.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 6: One two-factor setup page model for staff and residents (item 4, first half)

**Files:**
- Create: `Launcher/GridGate.Launcher/Security/TwoFactorSetupPageModel.cs`
- Modify: `Launcher/GridGate.Launcher/Pages/Guest/TwoFactor.cshtml.cs` (whole class body), `Launcher/GridGate.Launcher/Pages/Profile/TwoFactor.cshtml.cs` (whole class body)
- Test: none new (see Step 1)

**Interfaces:**
- Produces (namespace `GridGate.Launcher.Security`):
  - `public sealed record TwoFactorSetupTexts(string WrongCode, string TooManyAttempts, string SaveFailed, string StartedAgain, string StartedElsewhere)`
  - `public abstract class TwoFactorSetupPageModel(Func<LauncherSettings> settings, AuditLog audit, IDataProtectionProvider dp, LoginThrottle throttle, ILogger log) : PageModel`
    - Bound/public (names the views use): `Code`, `Shown` (`[BindProperty]`), `State`, `QrSvg`, `SecretText`, `BackupCodes`, `Error`, `Notice`.
    - Filled in by each page: `protected abstract TwoFactorStore Store { get; }`, `protected abstract string ShownPurpose { get; }`, `protected abstract SignInDoor Door { get; }`, `protected abstract string AuditRole { get; }`, `protected abstract string LogArea { get; }`, `protected abstract TwoFactorSetupTexts Texts { get; }`, `protected virtual Task TurnedOnAsync(Guid principal) => Task.CompletedTask`.
    - `protected LauncherSettings Settings() => settings();` — the pages read settings through this: a page that passes a primary-constructor parameter to the base and also uses it itself gets CS9124, an error here.
    - Shared steps: `protected void StartEnrol(Guid principal)`, `protected Task<IActionResult> TurnOnAsync(Guid principal)` (from `Unprotect(Shown)` through `audit two-factor-on` → `TurnedOnAsync` → `State = On` → `BackupCodes = codes`), `protected IActionResult TurnOff(Guid principal, Func<IActionResult> done)` (from `Locked()` through `audit two-factor-off`, then `done()`), `protected IActionResult NewCodes(Guid principal)` (from `Locked()` through `BackupCodes = codes`); private `Locked`, `Wrong`, `ShowSecret`, `Unprotect`, `Protector`, `AccountName`, `Failed`, `Entry` as both pages have them today. `Failed` logs `"{Area}: a change for first name {First} couldn't be saved: {Message}"` with `LogArea`.

- [ ] **Step 1: Confirm the existing tests pin both pages**

No new test: the code moves without changing what either page does, and `TwoFactorSignInTests` (staff: wrong code counts, lock, backup codes, turn off with a backup code, required switch, failed save, the enrolment form across the five-minute recheck) and `ResidentTwoFactorTests` (resident turn-on, page hidden or kept by the rule, guest throttle) already exercise every branch.
Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*TwoFactorSignInTests" --filter-class "*ResidentTwoFactorTests" --filter-class "*SignInPagesKeysTests" --filter-class "*GuestPagesHubTests" --filter-class "*GroupsPagesTests"`
Expected: Passed (baseline before the change).

- [ ] **Step 2: Write `TwoFactorSetupPageModel`**

- [ ] **Step 3: Make both pages fill it in**

Each page keeps: its handlers and their guards (who is signed in, the `State` checks, staff's `Required` check before `TurnOff`), its constructor's own services, and its doc comment.

| | Guest (`Pages/Guest/TwoFactor`) | Staff (`Pages/Profile/TwoFactor`) |
|---|---|---|
| not signed in / page hidden | `NotFound()` | `Challenge(AuthSchemes.Admin)` |
| `Store` | `residents.Store` | `store` |
| `ShownPurpose` (must not change) | `"GridGate.GuestTwoFactorSetup.v1"` | `"GridGate.TwoFactorSetup.v1"` |
| `Door` | `SignInDoor.Guest` | `SignInDoor.Staff` |
| `AuditRole` | `AuthSchemes.GuestRole` | the role of the claims it read in `Load` (kept in a `StaffClaims?` field) `.AuditName()` |
| `LogArea` | `"Resident two-factor"` | `"Two-factor"` |
| `Texts` | `new(t["Common.WrongCode"], t["Common.TooManyAttempts"], t["SignIn.SaveFailed"], t["TwoFactor.StartedAgain"], t["TwoFactor.StartedElsewhere"])` | `static readonly` `new(WrongCode, "Too many attempts. Try again in a few minutes.", TwoFactorStore.SaveFailed, "Setup started again. Scan the new code below.", "Setup was started again somewhere else. Scan the new code below.")` |
| `TurnedOnAsync` | (default) | when `HasEnrolClaim`: the new cookie and `MustEnrol = false`; then `TwoFactorGate.Forget(HttpContext)` |
| `TurnOff` `done` | `() => ResidentTwoFactor.PageShown(Settings(), TwoFactorState.Off) ? RedirectToPage() : LocalRedirect("/guest")` | `RedirectToPage` |

Staff keeps `public const string WrongCode` and `RequiredText`, `MustEnrol`, `Required`, `Home`. The guest `OnPostTurnOn` becomes `OnPostTurnOnAsync` (same handler name, `TurnOn`).

- [ ] **Step 4: Run the tests**

Same command as Step 1. Expected: Passed, failed 0.

- [ ] **Step 5: Commit**

```bash
git add Launcher/GridGate.Launcher/Security/TwoFactorSetupPageModel.cs Launcher/GridGate.Launcher/Pages/Guest/TwoFactor.cshtml.cs Launcher/GridGate.Launcher/Pages/Profile/TwoFactor.cshtml.cs
git commit -F - <<'EOF'
refactor: one two-factor setup page model for staff and residents

Purposes, texts, audit roles, log lines and each page's own steps stay as they were.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 7: One two-factor reset on the Security page (item 4, second half)

**Files:**
- Modify: `Launcher/GridGate.Launcher/Pages/Security/Index.cshtml.cs:89-160`

**Interfaces:**
- Produces: `private IActionResult Reset(TwoFactorStore store, Guid id, string damagedText, Action<Guid> signOut)` in `IndexModel` — the body of today's `OnPostReset`, with `twoFactor` → `store`, the damaged message → `damagedText`, `sessions.EndAll(…)` → `signOut(…)`.

- [ ] **Step 1: Baseline**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*SecurityPageTests" --filter-class "*ResidentTwoFactorTests" --filter-class "*TwoFactorSignInTests"`
Expected: Passed. (`ResidentTwoFactorTests.Owner_resets_resident_two_factor_from_security_page` and the staff reset tests in `SecurityPageTests` pin both handlers; no new test, the body only moves.)

- [ ] **Step 2: Write `Reset` and make both handlers call it**

`OnPostReset(id) => Reset(twoFactor, id, "The two-factor file is damaged and has not been changed. Run reset-two-factor --all-damaged on the grid computer.", sessions.EndAll)`; `OnPostResetResident(id) => Reset(residents.Store, id, "The residents' two-factor file is damaged and has not been changed.", guestStamps.Rotate)`. Keep the resident handler's doc comment and the "changed while it was being saved" comment.

- [ ] **Step 3: Run the tests**

Same command. Expected: Passed, failed 0.

- [ ] **Step 4: Commit**

```bash
git add Launcher/GridGate.Launcher/Pages/Security/Index.cshtml.cs
git commit -F - <<'EOF'
refactor: one two-factor reset body on the Security page

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 8: GridController operation lock in one place (item 5)

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Grid/GridController.cs` (the 29 `_ops.WaitAsync` sites at 135, 143, 162, 194, 228, 250, 270, 282, 389, 512, 569, 607, 678, 702, 735, 769, 820, 853, 891, 912, 929, 951, 968, 1009, 1036, 1094, 1133, 1293, 1395; `LiveRegion` reuse at 198-199, 393-394, 516-517, 1056-1057)
- Modify: `Launcher/GridGate.Launcher.Core/Grid/GridController.OtherGrid.cs:265-282`
- Test: `Launcher/GridGate.Launcher.Tests/GridControllerWiringTests.cs`

**Interfaces:**
- Produces (private, in `GridController.cs`):
  - `private async Task<T> ExclusiveAsync<T>(GridActivity? activity, Func<Task<T>> work, CancellationToken ct)`
  - `private Task ExclusiveAsync(GridActivity? activity, Func<Task> work, CancellationToken ct)`

  `activity` has three meanings, which the signature does not say:

```csharp
await _ops.WaitAsync(ct);
if (activity is { } a) _activity = a;                 // after the wait, before the try
try { return await work(); }
finally
{
    if (activity is not null) _activity = GridActivity.None;   // null: the operation never touches _activity
    _ops.Release();
}
```

  Pass the activity a site sets today; `GridActivity.None` for `MoveRegionAsync`, `ChangeRunsWithAsync` and `DeleteRegionAsync` (no activity at the start, `Restarting`/`Stopping` set part-way, reset to None at the end); `null` for sites that never set it; `RegionPowerAsync` works its activity out from `action` before the call.

- [ ] **Step 1: Write the pinning test and see it pass on the current code**

```csharp
[Fact]
public async Task A_failed_operation_frees_the_lock_and_clears_the_activity()
{
    var ct = TestContext.Current.CancellationToken;
    await Assert.ThrowsAsync<ArgumentException>(() => _grid.RestartComponentAsync("nonsense", ct));   // sets Restarting first
    Assert.Equal(GridActivity.None, _grid.GetStatus().Activity);
    // A lock left held would time out here instead.
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _grid.MoveRegionAsync(Guid.NewGuid(), 0, 0, ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
    Assert.Equal(GridActivity.None, _grid.GetStatus().Activity);
}
```

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*GridControllerWiringTests"`
Expected: Passed.

- [ ] **Step 2: Add `ExclusiveAsync` and convert the sites**

Convert a site only when its `finally` holds nothing but `_activity = GridActivity.None;` and/or `_ops.Release();` and `_activity` is set (if at all) straight after the wait; otherwise leave it and add it to the PR notes. Code before the wait (argument checks in `RenameRegionAsync`, `StartComponentAsync`, `SetEstateOwnerAsync` …) stays before the call. Bodies with no `await` (`BeginIarJobAsync`, `BeginOarJobAsync`, …) pass `() => Task.FromResult(…)` or `() => { …; return Task.CompletedTask; }`, never an `async` lambda without `await` (CS1998 is an error here).

- [ ] **Step 3: Reuse `LiveRegion`**

The two-line "no such region / has been deleted" lookups at 198-199, 393-394, 516-517 and 1056-1057 become `LiveRegion(s, regionId)`. Leave `BeginOarJobAsync` 1399-1400 (it throws `ArgumentException`, unlike `LiveRegion`) and 573, 611, 1040 (they want deleted regions too).

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*GridController*" --filter-class "*OtherGridTests" --filter-class "*RegionPanelTests" --filter-class "*MoveRegionPageTests" --filter-class "*RunsChangePageTests" --filter-class "*DeletePageTests" --filter-class "*RegionSettingsPageTests" --filter-class "*OarJobPagesTests" --filter-class "*IarJobsTests" --filter-class "*AddRegionPageTests" --filter-class "*GridJoinTests" --filter-class "*ConnectedServerControllerTests" --filter-class "*RegionServerControllerTests"`
Expected: Passed, failed 0.

- [ ] **Step 5: Commit**

```bash
git add Launcher/GridGate.Launcher.Core/Grid/GridController.cs Launcher/GridGate.Launcher.Core/Grid/GridController.OtherGrid.cs Launcher/GridGate.Launcher.Tests/GridControllerWiringTests.cs
git commit -F - <<'EOF'
refactor: GridController's operation lock in one ExclusiveAsync

The activity is still set after the wait and reset at the end; operations
without one never touch it. LiveRegion is reused where it was written out.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 9: One pending two-factor sign-in cookie (item 6, first half)

**Files:**
- Create: `Launcher/GridGate.Launcher/Security/PendingSignInCookie.cs`
- Modify: `Launcher/GridGate.Launcher/Security/TwoFactorPending.cs:24-71`, `Launcher/GridGate.Launcher/Guests/GuestTwoFactorPending.cs:22-67` (the records gain `: IPendingSignIn`)
- Test: `Launcher/GridGate.Launcher.Tests/TwoFactorSignInTests.cs`, `Launcher/GridGate.Launcher.Tests/ResidentTwoFactorTests.cs`

**Interfaces:**
- Produces (namespace `GridGate.Launcher.Security`):
  - `public interface IPendingSignIn { string First { get; } string Last { get; } long Expires { get; } }` (implementing it adds no JSON property)
  - `public sealed class PendingSignInCookie<T>(string name, string path, string purpose, TimeSpan lifetime, Func<T, bool>? accept = null) where T : class, IPendingSignIn` with `void Issue(HttpContext ctx, Func<long, T> make)` (`make` gets the expiry in Unix seconds), `T? Read(HttpContext ctx)` (null when missing, unreadable, `First`/`Last` empty, `accept` false, or expired), `void Delete(HttpContext ctx)`; cookie options as today (HttpOnly, SameSite Strict, `Path = path`, `Secure = ctx.Request.IsHttps`, IsEssential).
- The two static classes keep their public members (`CookieName`, `CookiePath`, `Lifetime`, `Issue`, `Read`, `Delete`) and hold a `private static readonly PendingSignInCookie<…> Cookie` declared **after** `Lifetime` (static fields initialise in text order). Staff passes `accept: p => p.Role != StaffRole.None`.

- [ ] **Step 1: Write the pinning tests and see them pass on the current code**

`TwoFactorSignInTests.A_code_step_cookie_from_before_the_change_is_still_read`:

```csharp
await using var f = Factory();
var expires = f.Time.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds();
var json = JsonSerializer.Serialize(new TwoFactorPendingSignIn(Guid.NewGuid(), "Test", "Admin", 200, StaffRole.Admin, null, expires));
var value = f.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("GridGate.TwoFactorPending.v1").Protect(json);
using var request = new HttpRequestMessage(HttpMethod.Get, "/login/code");
request.Headers.Add("Cookie", "gg-2fa=" + value);
using var r = await f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false }).SendAsync(request, Ct);
Assert.Equal(HttpStatusCode.OK, r.StatusCode);
```

`ResidentTwoFactorTests.A_guest_code_step_cookie_from_before_the_change_is_still_read`: the same with `GuestTwoFactorPendingSignIn(…, expires)`, purpose `"GridGate.GuestTwoFactorPending.v1"`, cookie `gg-guest-2fa`, page `/guest/sign-in/code`.

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*TwoFactorSignInTests" --filter-class "*ResidentTwoFactorTests"`
Expected: Passed.

- [ ] **Step 2: Write `PendingSignInCookie<T>` and make both static classes wrappers over it**

- [ ] **Step 3: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*TwoFactorSignInTests" --filter-class "*ResidentTwoFactorTests" --filter-class "*LoginTests" --filter-class "*GuestSignInTests"`
Expected: Passed, failed 0.

- [ ] **Step 4: Commit**

```bash
git add Launcher/GridGate.Launcher/Security/PendingSignInCookie.cs Launcher/GridGate.Launcher/Security/TwoFactorPending.cs Launcher/GridGate.Launcher/Guests/GuestTwoFactorPending.cs Launcher/GridGate.Launcher.Tests/TwoFactorSignInTests.cs Launcher/GridGate.Launcher.Tests/ResidentTwoFactorTests.cs
git commit -F - <<'EOF'
refactor: one pending two-factor sign-in cookie for staff and residents

Cookie names, paths and data-protection purposes are unchanged, so cookies
already issued still read.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 10: One two-factor code step (item 6, second half)

**Files:**
- Create: `Launcher/GridGate.Launcher/Security/TwoFactorCodeStep.cs`
- Modify: `Launcher/GridGate.Launcher/Pages/Guest/SignInCode.cshtml.cs:31-88`, `Launcher/GridGate.Launcher/Pages/Login/Code.cshtml.cs:35-92`

**Interfaces:**
- Produces (namespace `GridGate.Launcher.Security`):
  - `public enum CodeStepResult { SignedIn, Locked, TwoFactorOff, TwoFactorUnknown, SaveFailed, WrongCode }`
  - `public sealed record CodeStepCaller(Guid Principal, string Name, string First, string AuditRole)`
  - `public static Task<CodeStepResult> TwoFactorCodeStep.VerifyAsync(TwoFactorStore store, LoginThrottle throttle, AuditLog audit, ILogger log, string logArea, SignInDoor door, CodeStepCaller who, string? code, IPAddress? remote, Func<Task> signIn)` — in today's order: throttle locked → `Locked`; state Off → `TwoFactorOff`; Unknown → `TwoFactorUnknown`; `VerifyCode` throws IO/UnauthorizedAccess → warning `"{Area}: a two-factor code for first name {First} couldn't be marked used: {Message}"` → `SaveFailed`; wrong → `RecordFailure`, information `"{Area}: a two-factor code for first name {First} was refused"`, audit `sign-in-refused`/`two-factor code`/`refused` → `WrongCode`; right → `RecordSuccess`, `signIn()`, audit `sign-in`, then `backup-code-used` when a backup code was used → `SignedIn`. The throttle key is `AvatarName.Parse(who.Name).Key`; `First` is logged through `LogText.Clean(who.First, 32)`.

- [ ] **Step 1: Baseline**

No new test: the step moves unchanged, and `TwoFactorSignInTests` (wrong code counts toward the lock, backup code once, same code refused in another browser, reset meanwhile, code that can't be saved, expiry) and `ResidentTwoFactorTests` (guest door asks the code, wrong guest code counts in the guest throttle, backup once, no password step goes back) pin every outcome at both doors.
Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*TwoFactorSignInTests" --filter-class "*ResidentTwoFactorTests" --filter-class "*SignInPagesKeysTests"`
Expected: Passed.

- [ ] **Step 2: Write `TwoFactorCodeStep` and map both pages onto it**

| | Guest `SignInCode` | Staff `Login/Code` |
|---|---|---|
| `logArea`, `door` | `"Guest sign-in"`, `SignInDoor.Guest` | `"Sign-in"`, `SignInDoor.Staff` |
| `AuditRole` | `AuthSchemes.GuestRole` | `pending.Role.AuditName()` |
| `signIn` | `GuestTwoFactorPending.Delete` then `GuestSignIn.SignInAsync(HttpContext, pending.Account)` | `TwoFactorPending.Delete` then `StaffSignIn.SignInAsync(HttpContext, pending.Account, pending.Role)` |
| Locked / SaveFailed / WrongCode → `Error =` | `t["Common.TooManyAttempts"]` / `t["SignIn.SaveFailed"]` / `t["Common.WrongCode"]` | `"Too many attempts. Try again in a few minutes."` / `TwoFactorStore.SaveFailed` / `WrongCode` |
| TwoFactorOff | `BackToPassword()` (`/guest/sign-in`) | `BackToPassword()` (`/login`) |
| TwoFactorUnknown | `TwoFactorUnknown = true; Page()` | same |
| SignedIn | `LocalRedirect("/guest")` | `LocalRedirect(LoginModel.Landing(pending.Role, pending.ReturnUrl, Url))` |

Each page passes its own `ILogger<…>`, so log categories stay.

- [ ] **Step 3: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*TwoFactorSignInTests" --filter-class "*ResidentTwoFactorTests" --filter-class "*SignInPagesKeysTests" --filter-class "*LoginTests" --filter-class "*GuestSignInTests"`
Expected: Passed, failed 0.

- [ ] **Step 4: Commit**

```bash
git add Launcher/GridGate.Launcher/Security/TwoFactorCodeStep.cs Launcher/GridGate.Launcher/Pages/Guest/SignInCode.cshtml.cs Launcher/GridGate.Launcher/Pages/Login/Code.cshtml.cs
git commit -F - <<'EOF'
refactor: one two-factor code step for the staff and guest doors

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 11: Call the helpers that already exist (item 7, first half)

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/OpenSim/RobustClient.cs` (extension after the `RobustAccount` record)
- Modify (website address): `Launcher/GridGate.Launcher/Guests/AccountChanges.cs:289`, `SignUpApprovals.cs:167`, `SignUps.cs:336` (delete each `Site`, call `WebsiteAddress.Base`), `PasswordResets.cs:122, 147`, `SignUpNotices.cs:52`, `Launcher/GridGate.Launcher/Security/PeopleActions.cs:160` (→ `WebsiteAddress.ForgotPassword(s)`), `Launcher/GridGate.Launcher/CommandLine.cs:124`
- Modify (full name): delete `Full(RobustAccount)` in `Estates/EstatesAdmin.cs:207`, `Groups/GroupsAdmin.cs:217`, `Guests/AccountChanges.cs:287`, `Guests/SignUpApprovals.cs:169`, `Guests/SignUps.cs:338`; inline copies on a `RobustAccount` in `Guests/GuestSignIn.cs:24`, `Guests/PasswordResets.cs:123, 148, 211`, `Guests/SignUpCleanup.cs:70, 97`, `Guests/SignUpNotices.cs:59`, `Guests/TicketSweep.cs:134`, `Pages/Guest/ResetPassword.cshtml.cs:75`, `Pages/Login.cshtml.cs:82, 94`, `Pages/People/Index.cshtml.cs:88`, `Pages/Regions/Region.cshtml.cs:240`, `Pages/Staff/Index.cshtml.cs:82`, `Security/PeopleActions.cs:20`, `Security/StaffActions.cs:73`, `Security/StaffSignIn.cs:47`, `Launcher/GridGate.Launcher.Core/Api/LegacyCommandApi.cs:116`
- Modify (take-down access): `Launcher/GridGate.Launcher/Pages/People/Delete.cshtml.cs:21, 33, 52`, `Pages/People/Edit.cshtml.cs:26, 36, 50`
- Test: `Launcher/GridGate.Launcher.Tests/RobustClientTests.cs`

**Interfaces:**
- Consumes: `WebsiteAddress.Base(LauncherSettings) : string`, `WebsiteAddress.ForgotPassword(LauncherSettings) : string` (`Launcher/GridGate.Launcher.Core/Config/WebsiteAddress.cs`), `TakedownAccess.MayUse(ClaimsPrincipal) : bool` (`Pages/People/TakedownAccess.cs`).
- Produces: `public static string FullName(this RobustAccount a)` in `public static class RobustAccountNames` (namespace `GridGate.Launcher.Core.OpenSim`) — `$"{a.FirstName} {a.LastName}"`. An extension method, not a property: a property on a record serialised into a cookie would add a field.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Full_name_is_first_space_last() =>
    Assert.Equal("Ann Smith", new RobustAccount(Guid.Empty, "Ann", "Smith", 0, "").FullName());
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*RobustClientTests"`
Expected: build error CS1061 "'RobustAccount' does not contain a definition for 'FullName'".

- [ ] **Step 3: Add `RobustAccountNames.FullName`**

- [ ] **Step 4: Replace the copies**

Only replace `$"{x.FirstName} {x.LastName}"` where `x` is a `RobustAccount`. Leave the ones on other types: `OwnerInfo` (`Pages/Setup/Connect.cshtml.cs:26`, `Core/Config/GridConfigWriter.cs:152`), `IarJobRequest` (`Core/Grid/GridController.cs:1321`), `StarterImport.cs:96`, and the `Name` property of the two pending sign-in records. The `Robust` public-port addresses (`GridController.cs:1322`, `Plans/GridEndpoints.cs:63-64`) are not the website address; leave them.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*RobustClientTests" --filter-class "*AccountChangesTests" --filter-class "*SignUpsTests" --filter-class "*SignUpApprovalsTests" --filter-class "*SignUpNoticesTests" --filter-class "*SignUpCleanupTests" --filter-class "*TicketSweepTests" --filter-class "*PasswordResetTests" --filter-class "*PeopleActionsTests" --filter-class "*CommandLineTests" --filter-class "*EstatesPagesTests" --filter-class "*GroupsAdminTests" --filter-class "*PeopleDeleteTests" --filter-class "*PeoplePagesTests" --filter-class "*DeletePageTests" --filter-class "*LoginTests" --filter-class "*GuestSignInTests" --filter-class "*StaffPagesTests" --filter-class "*StaffChangesTests" --filter-class "*LegacyCommandApiTests" --filter-class "*RegionOwnerCardTests"`
Expected: Passed, failed 0.

- [ ] **Step 6: Commit**

```bash
git add Launcher/GridGate.Launcher Launcher/GridGate.Launcher.Core Launcher/GridGate.Launcher.Tests/RobustClientTests.cs
git commit -F - <<'EOF'
refactor: call WebsiteAddress, RobustAccount.FullName and TakedownAccess

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 12: Shared avatar lookup and owner email (item 7, second half)

**Files:**
- Modify: `Launcher/GridGate.Launcher/Estates/AvatarLookup.cs`
- Modify: `Launcher/GridGate.Launcher/Pages/Groups/Group.cshtml.cs:44-64` (`OnPostAddAsync` lookup), `Launcher/GridGate.Launcher/Pages/Staff/Index.cshtml.cs:44-62` (`OnPostChangeAsync` lookup)
- Create: `Launcher/GridGate.Launcher/Email/OwnerEmail.cs`
- Modify: `Launcher/GridGate.Launcher/Pages/Security/Email.cshtml.cs:226-239`, `EmailWording.cshtml.cs:181-194` (delete `OwnerEmailAsync`, call `OwnerEmail.FindAsync`)
- Test: `Launcher/GridGate.Launcher.Tests/AvatarLookupTests.cs` (new)

**Interfaces:**
- Consumes: `RobustFailure.IsUnreachable` (Task 1).
- Produces:
  - `AvatarLookup.FindAsync(IRobustClient robust, string? typed, ILogger log, CancellationToken ct, string area = "Estates") : Task<(RobustAccount? Account, string? Problem)>` — logs `"{Area}: the account service did not answer: {Message}"`.
  - `public static class OwnerEmail` (namespace `GridGate.Launcher.Email`): `Task<string?> FindAsync(IRobustClient robust, LauncherSettings s, CancellationToken ct)` — today's `OwnerEmailAsync` body with `settings()` → `s`, its filter (`HttpRequestException or TaskCanceledException or InvalidOperationException`) kept as is.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task A_lookup_that_gets_no_answer_says_so_under_the_callers_name()
{
    var logs = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
    var log = new CapturingLoggerProvider(logs).CreateLogger("test");
    var (account, problem) = await AvatarLookup.FindAsync(new FakeRobustAccounts { Down = true }, "Ann Smith", log,
        TestContext.Current.CancellationToken, "Groups");
    Assert.Null(account);
    Assert.Equal(StaffActions.RobustDown, problem);
    Assert.Equal("Groups: the account service did not answer: Connection refused", Assert.Single(logs).Message);
}
```

Plus `Typing_one_word_asks_for_both_names`: `FindAsync(…, "Ann", …)` → `(null, "Type the avatar's first and last name.")`.

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*AvatarLookupTests"`
Expected: build error CS1739/CS1501 (no `area` parameter).

- [ ] **Step 3: Add the `area` parameter, write `OwnerEmail`, replace the copies**

Group: `var (account, problem) = await AvatarLookup.FindAsync(robust, avatar, log, ct, "Groups");` then `outcome = account is null ? new GroupOutcome(false, problem!) : await admin.AddMemberAsync(User, Ip, id, account, ct);`. Staff: inside the `else` after the change check, `FindAsync(robust, name, log, ct, "Staff")`, `Error = problem` when no account. Texts are the same three as today.

Leave: `Pages/Regions/Region.cshtml.cs:214-239` (its own texts: "There's no avatar called … on this grid." and `RegionPanelModel.RobustDown`); `Pages/People/Index.cshtml.cs:186-197` (sets `Error ??=`, its own flow).

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*AvatarLookupTests" --filter-class "*GroupsPagesTests" --filter-class "*StaffPagesTests" --filter-class "*StaffChangesTests" --filter-class "*EstatesPagesTests" --filter-class "*EmailPageTests" --filter-class "*EmailWordingPageTests"`
Expected: Passed, failed 0.

- [ ] **Step 5: Commit**

```bash
git add Launcher/GridGate.Launcher/Estates/AvatarLookup.cs Launcher/GridGate.Launcher/Email/OwnerEmail.cs Launcher/GridGate.Launcher/Pages Launcher/GridGate.Launcher.Tests/AvatarLookupTests.cs
git commit -F - <<'EOF'
refactor: Groups and Staff use AvatarLookup; one OwnerEmail lookup

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 13: State-file steps in `AtomicFile` (item 8)

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/AtomicFile.cs`
- Modify (shared read): `Guests/PendingEmailChanges.cs:73-75`, `Guests/SignUpBoard.cs:153-155`, `Languages/OwnerLanguages.cs:164-166`, `Security/LoginThrottle.cs:166-168`, `Settings/SettingsStore.cs:38-40`, `JsonStateFile.cs:29-31`
- Modify (set aside): `Guests/PendingEmailChanges.cs:94-99`, `Guests/SignUpBoard.cs:182-187`, `Languages/OwnerLanguages.cs:202-214`, `JsonStateFile.cs:78-90`
- Modify (create folder, then write): `Guests/ApprovalNotices.cs:69-70`, `Tickets/TicketNotices.cs:92-93`, `Guests/PendingEmailChanges.cs:101-102`, `Guests/SignUpBoard.cs:189-190`, `JsonStateFile.cs:63-64` (all under `Launcher/GridGate.Launcher.Core/`)
- Test: `Launcher/GridGate.Launcher.Tests/AtomicFileTests.cs` (new)

**Interfaces:**
- Produces (on `public static class AtomicFile`):
  - `string ReadShared(string path)` — `FileShare.ReadWrite | FileShare.Delete`, read to end; missing file or folder throws as today (callers keep their catches).
  - `string SetAside(string path)` — moves to `path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)` with overwrite and returns that path; a failed move throws.
  - `void WriteAllTextCreatingFolder(string path, string text)` — creates the folder when `Path.GetDirectoryName(Path.GetFullPath(path))` is non-empty, then `WriteAllText`. `WriteAllText` itself is unchanged (TakedownStore relies on it needing the folder).

- [ ] **Step 1: Write the failing tests**

`AtomicFileTests` (temp folder per test, as `JsonStateFileTests` does):
- `Shared_read_reads_while_another_handle_writes`: open the file with `FileAccess.Write, FileShare.ReadWrite | FileShare.Delete`, write "a"; `Assert.Equal("a", AtomicFile.ReadShared(path))`.
- `Shared_read_of_a_missing_file_throws`: `Assert.Throws<FileNotFoundException>(() => AtomicFile.ReadShared(missing))`.
- `Set_aside_moves_the_file_to_a_damaged_name`: returns a path matching `@"state\.json\.damaged-\d{14}$"`, original gone, content kept.
- `Set_aside_of_a_missing_file_throws`: `Assert.Throws<FileNotFoundException>`.
- `Write_creating_the_folder_makes_missing_folders`: path two folders deep; afterwards `File.ReadAllText(path) == "x"`.
- `Plain_write_still_needs_its_folder`: `Assert.Throws<DirectoryNotFoundException>(() => AtomicFile.WriteAllText(deep, "x"))`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*AtomicFileTests"`
Expected: build error CS0117 "'AtomicFile' does not contain a definition for 'ReadShared'".

- [ ] **Step 3: Implement the three methods; run the tests to see them pass**

Same command. Expected: Passed, 6 tests.

- [ ] **Step 4: Point the copies at them**

Each store keeps its own catch blocks, log text and failure behaviour: a failed set-aside still throws in PendingEmailChanges and SignUpBoard and is still logged in OwnerLanguages and JsonStateFile; SignUpBoard keeps its `File.Exists` check before the set-aside; TicketNotices still swallows IO errors and ApprovalNotices doesn't; the stores still re-read on every call and throw on a failed write (why they aren't `JsonStateFile`). The log lines use `Path.GetFileName(aside)` as today.

Leave: `Launcher/GridGate.Launcher/Security/StaffSessions.cs:164`, `SuspendedStore.cs:54`, `TwoFactorStore.cs:254` (same shared read, not in the review's list: PR notes), the log readers (`Logs/*`, `AuditLog.cs:48`), `CommandLine.cs:319`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*AtomicFileTests" --filter-class "*JsonStateFileTests" --filter-class "*PendingEmailChangesTests" --filter-class "*SignUpBoardTests" --filter-class "*OwnerLanguagesTests" --filter-class "*LoginThrottleTests" --filter-class "*SettingsStoreTests" --filter-class "*ApprovalFlowTests" --filter-class "*SupportPagesTests" --filter-class "*TicketStoreTests" --filter-class "*TakedownStoreTests"`
Expected: Passed, failed 0.

- [ ] **Step 6: Commit**

```bash
git add Launcher/GridGate.Launcher.Core Launcher/GridGate.Launcher.Tests/AtomicFileTests.cs
git commit -F - <<'EOF'
refactor: shared read, set-aside and folder-making write in AtomicFile

Each store keeps its own failure handling and log lines.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 14: Region background actions and Regions page redirects (item 9)

**Files:**
- Modify: `Launcher/GridGate.Launcher/Endpoints/RegionActions.cs:115-162` (`RunDeleted` → `Run`; `TryRun` calls it)
- Modify: `Launcher/GridGate.Launcher/OarJobs.cs` (add `Start`)
- Modify: `Launcher/GridGate.Launcher/Pages/Regions/Region.cshtml.cs` (the `{ Notice = …; return Redirect("/regions/region/" + id); }` pairs at 126-127, 154-155, 175-176, 192-193, 283-284, 288-289; the OAR starts at 158-161 and 189-198), `Pages/Regions/Import.cshtml.cs:72-77`, `Pages/Regions/Settings.cshtml.cs:176, 202-205`, `Pages/Regions/Move.cshtml.cs:49`, `Pages/Regions/Runs.cshtml.cs:52`
- Test: `Launcher/GridGate.Launcher.Tests/RegionActionsTests.cs` (new)

**Interfaces:**
- Produces:
  - `public static string RegionActions.BusyNotice(string what)` — `$"{what} is still running. Try again when it finishes."`
  - `private void RegionActions.Run(string what, string name, Func<CancellationToken, Task> work)` — today's `RunDeleted` plus `TryRun`'s `catch (PortConflictException e) { log.LogCritical("{Message}", e.Message); }` before the general catch. `TryRun` calls `Run(action.ToString(), region.Name, stopping => grid.RegionPowerAsync(id, action, stopping))` (the rendered log lines and the job name `region {what} {name}` are the same as today's `{Action}` ones).
  - `public string? OarJobs.Start(OarJobRequest request, string regionName, Func<CancellationToken, Task>? after = null)` — `TryStart(request, Describe(request, regionName), after)`; null when it started or no job is running any more, else `RegionActions.BusyNotice(running.Text)`. Callers: `if (oarJobs.Start(request, Name, after) is { } busy) Notice = busy;` then their own redirect.
  - `private RedirectResult RegionPanelModel.Back(Guid id, string notice)` — sets `Notice`, returns `Redirect("/regions/region/" + id)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Busy_notice_says_what_is_running() =>
    Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", RegionActions.BusyNotice("Saving Home as an OAR"));
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*RegionActionsTests"`
Expected: build error CS0117 "'RegionActions' does not contain a definition for 'BusyNotice'".

- [ ] **Step 3: Check that the new catch is unreachable for delete, restore and erase**

Run: `grep -n "PrepareAsync(" Launcher/GridGate.Launcher.Core/Grid/GridController.cs`
Expected: only the definition (1681) and the calls in `StartCoreAsync` (647) and `StartComponentAsync(string, …)` (750), neither reached from `DeleteRegionAsync`, `RestoreRegionAsync` or `EraseRegionAsync`; `PortConflictException` is only thrown in `PrepareAsync`. If that is no longer true, keep `RunDeleted` without the catch and add it to the PR notes.

- [ ] **Step 4: Implement and replace the copies**

All 11 "is still running" texts go through `RegionActions.BusyNotice`; `Import.Start`, the two OAR starts in Region and the one in Settings go through `OarJobs.Start`; the six adjacent `Notice =` / `return Redirect("/regions/region/" + id);` pairs in Region become `return Back(id, …);`.

Leave: `TryMove` and `TryChangeRunsWith` (their log wording differs; folding them in would change it); `GridActions.cs:52-71` ("Dashboard:" lines with no region); Settings' redirects to `…/settings/basics`; the redirects in `Move.cshtml.cs` and `Runs.cshtml.cs` (other page classes, not in the review's count).

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release --filter-class "*RegionActionsTests" --filter-class "*RegionPanelTests" --filter-class "*MoveRegionPageTests" --filter-class "*RunsChangePageTests" --filter-class "*DeletePageTests" --filter-class "*OarJobPagesTests" --filter-class "*RegionSettingsPageTests" --filter-class "*LibraryPageTests" --filter-class "*RegionsPageTests"`
Expected: Passed, failed 0.

- [ ] **Step 6: Commit**

```bash
git add Launcher/GridGate.Launcher/Endpoints/RegionActions.cs Launcher/GridGate.Launcher/OarJobs.cs Launcher/GridGate.Launcher/Pages/Regions Launcher/GridGate.Launcher.Tests/RegionActionsTests.cs
git commit -F - <<'EOF'
refactor: one region background runner, one busy notice, one OAR start

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 15: Mesh importer bounds and sub-faces (item 10)

**Ruling (controller, 2026-11-13):** these files live under `OpenSim/Tools/GridGate.ModelImport/`, GridGate's own converter (namespace `GridGate.ModelImport`), so the spec's limit on editing upstream OpenSim code does not apply. Do this task.

**Files:**
- Create: `OpenSim/Tools/GridGate.ModelImport/Model/MeshParts.cs` (the tool has `ImplicitUsings` off: write the `using System; …` lines as `FaceSplitter.cs` does)
- Modify: `Building/UnitMesh.cs:49-56` (`Box`), `Checking/ModelChecker.cs:148-156` (`LocalBox`), `Building/Simplifier.cs:38-45` (`Diagonal`), `Building/Simplifier.cs:75-99` (`Compact`), `Checking/FaceSplitter.cs:79-100` (`Piece`), `Building/MeshAssetWriter.cs:193-211` (`BoundingBox`) (all under `OpenSim/Tools/GridGate.ModelImport/`)
- Test: `tests/GridGate.ModelImport.Tests/MeshPartsTests.cs` (new)

**Interfaces:**
- Produces (`public static class GridGate.ModelImport.Model.MeshParts`):
  - `(Vector3 Lo, Vector3 Hi, bool Any) Bounds(IEnumerable<ImportFace> faces)` — the existing min/max loop over every position; `(Vector3.Zero, Vector3.Zero, false)` when there is none.
  - `ImportFace Piece(ImportFace face, IReadOnlyList<int> used, IReadOnlyList<int> triangles)` — today's `FaceSplitter.Piece` (the listed old vertices in that order, with UVs and weights when the face has them; triangles already numbered for the piece).

- [ ] **Step 1: Write the failing tests**

- `Bounds_of_no_vertices_is_zero_and_none`: `MeshParts.Bounds([])` and `Bounds([face with no positions])` → `(Vector3.Zero, Vector3.Zero, false)`.
- `Bounds_span_every_face`: two faces at (0,0,0)-(1,1,0) and (-1,2,3) → `Lo = (-1,0,0)`, `Hi = (1,2,3)`, `Any = true`.
- `Piece_keeps_the_listed_vertices_in_order`: a 4-vertex face with UVs and weights; `Piece(face, [3, 1], [0, 1, 0])` → positions `[p3, p1]`, normals `[n3, n1]`, UVs `[uv3, uv1]`, joints/weights of 3 then 1, triangles `[0, 1, 0]`, same material.
- `Piece_of_a_face_without_uvs_or_weights_has_none`: `Uv0` and `Weights` null.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/GridGate.ModelImport.Tests -c Release --filter-class "*MeshPartsTests"`
Expected: build error CS0103 "The name 'MeshParts' does not exist".

- [ ] **Step 3: Implement `MeshParts`; run the tests to see them pass**

Same command. Expected: Passed, 4 tests.

- [ ] **Step 4: Replace the copies**

- `UnitMesh.Box(part)`: `var (lo, hi, _) = MeshParts.Bounds(part.Mesh.Faces); return (lo, hi);` (an empty mesh still gives zero vectors).
- `ModelChecker.LocalBox(part)`: `MeshParts.Bounds(part.Mesh.Faces)`.
- `Simplifier.Diagonal(mesh)`: `var (lo, hi, _) = MeshParts.Bounds(mesh.Faces); return lo.X <= hi.X ? Vector3.Distance(lo, hi) : 0;` (keep the `lo.X <= hi.X` test, so an empty mesh still gives 0).
- `Simplifier.Compact`: keeps building `map` and `used`, then `return MeshParts.Piece(face, used, triangles.Select(v => map[v]).ToArray());`.
- `FaceSplitter`: delete `Piece`, `Flush` calls `MeshParts.Piece(face, used, triangles)`.
- `MeshAssetWriter.BoundingBox`: keep the `Aggregate` min/max (an empty mesh still throws there), build the map as `var map = Domain(min, max); map["BoundingVerts"] = OSD.FromBinary(corners); return map;` (same keys in the same order).

Leave: `ModelChecker.Overall` (it transforms each vertex first).

- [ ] **Step 5: Run the tests**

Run: `bash tests/GridGate.ModelImport.Tests/fetch-models.sh` (once; a test skips without its model), then `dotnet test --project tests/GridGate.ModelImport.Tests -c Release`
Expected: Passed, failed 0.

- [ ] **Step 6: Commit**

```bash
git add OpenSim/Tools/GridGate.ModelImport tests/GridGate.ModelImport.Tests/MeshPartsTests.cs
git commit -F - <<'EOF'
refactor(model-import): one bounds helper and one sub-face copy

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 16: Full verification

**Files:** none changed.

- [ ] **Step 1: Build everything**

Run: `dotnet build -c Release GridGate.sln`
Expected: `Build succeeded.` with 0 errors (warnings are errors in the launcher projects).

- [ ] **Step 2: Full launcher unit suite**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`
Expected (about 15 minutes): failed 0, skipped 10, passed at least 3,957 plus the tests this plan adds.

- [ ] **Step 3: Model import tests**

Run: `dotnet test --project tests/GridGate.ModelImport.Tests -c Release`
Expected: failed 0.

- [ ] **Step 4: Integration tests, when MariaDB is available**

Run (with `GRIDGATE_MARIADB_BIN` set to a MariaDB 11.4 `bin` folder): `dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release`
Expected: failed 0 (23–31 minutes). Without MariaDB, note in the PR that CI's launcher jobs run them.

- [ ] **Step 5: CI guards**

Run: `bash .github/scripts/check-retired-names.sh && bash .github/scripts/check-old-name.sh`
Expected: exit 0, no matches.

---

### Task 17: Tick the work list and collect the PR notes

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md:257-291` (items 1–10 and `[?]`)

- [ ] **Step 1: Tick each item with one line**

Turn each `- [ ] **N.` (and `- [ ] **[?]**`) into `- [x]` and add one indented line `  - Done 2026-11-…: …` saying what was done, for example:
- 1: "`GridDb` opens every robust/opensim connection and holds `RunAsync`/`Command`/`ExecAsync`/`ScalarAsync`; ids parse with `InventoryStore.IdOf`."
- 2: "`<error-box>`, `<ok-box>`, `<notice-box>` tag helpers (`Navigation/StatusBoxes.cs`); the variant boxes stay inline."
- 3: "`AuditLog.Write(actor, ip, action, target, result)` extension on `AuditActor`."
- 4: "`TwoFactorSetupPageModel` behind both two-factor pages; Security's two resets share `Reset`."
- 5: "`GridController.ExclusiveAsync`; `LiveRegion` reused."
- 6: "`PendingSignInCookie<T>` behind both pending cookies; `TwoFactorCodeStep.VerifyAsync` behind both code pages."
- 7: "`WebsiteAddress`, `RobustAccount.FullName()`, `TakedownAccess.MayUse`, `AvatarLookup` (with area) and `OwnerEmail` used everywhere."
- 8: "`AtomicFile.ReadShared`, `SetAside`, `WriteAllTextCreatingFolder`."
- 9: "`RegionActions.Run` and `BusyNotice`, `OarJobs.Start`, the Region page's `Back`."
- 10: "`MeshParts.Bounds` and `MeshParts.Piece`".
- `[?]`: "Decided: `RobustFailure.IsUnreachable` (five types, caller not cancelled); staff-list sites add their exception; `AccountChanges` moved to it (the one behaviour change)."

- [ ] **Step 2: Write the PR notes**

Collect, for the PR body: every copy left as it is with its reason (the "Leave" lines of Tasks 1–15, plus any site Task 8 Step 2 or Task 14 Step 3 skipped), the new duplicates found on the way (`StaffSessions`, `SuspendedStore`, `TwoFactorStore` shared reads; the redirect pairs in `Regions/Move` and `Regions/Runs`), the review's "smaller copies" left for later, and the open question on `OwnerEmailAsync`'s filter (a Robust lookup that does not catch `IOException`/`InvalidDataException` and answers null when cancelled).

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md
git commit -F - <<'EOF'
docs: tick the duplicate-code clean-up items in the work list

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```
