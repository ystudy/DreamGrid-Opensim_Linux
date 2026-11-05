# GridGate menu and quick fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build steps 1 (quick fixes) and 2 (the grouped menu) of the GridGate menu, looks and workflows design.

**Architecture:** A plain C# menu model (`StaffMenu`) decides what each person sees from their role, the plan and the waiting count. `_Layout.cshtml` only renders it, as Bootstrap 5.3 dropdowns whose headings are also links to hub pages. One new hub page (`/guest-pages`) is added. The renames are text changes.

**Tech Stack:** ASP.NET Core Razor Pages, Bootstrap 5.3.3 (bundle already loaded), xUnit + AngleSharp (`LauncherFactory`, `SplashPictureApiTests.Factory` / `StaffAsync`).

**Spec:** `docs/superpowers/specs/2026-11-05-gridgate-menu-looks-workflows-design.md` (§1, and step 1 of "The steps").

## Global Constraints
- No inline scripts or styles (CSP). Classes go in `wwwroot/css/site.css`.
- The menu is never a guard. No access rule in `Program.cs` changes, except adding the new `/GuestPages` folder's `GuestSideOnly` filter.
- It works without scripts: every group heading is an `<a href>` to its hub.
- English only. Write whole sentences in plain words.
- The `/advanced` and `/profile/emails` addresses stay the same.

## Review Focus
- **A two-factor-enrol-only session must still see only Two-factor and Sign out.** Tested in Task 2 (the existing `TwoFactorSignInTests` keep passing).
- **Exactly one `#signups-badge`.** Put it on Home when Home shows, else on the Sign-ups item. The existing `SignUpBoardPagesTests` badge tests pin this.
- **A group whose only item is its own hub keeps the group name.** A supervisor who can't approve sign-ups sees "People". Unit test in Task 2.
- **Region Server and Connected:** no People, Guest pages or Email notices. Unit tests in Task 2.
- **The active group is marked active** when you're on any page inside it. Unit test in Task 2.

---

### Task 1: Quick fixes

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Hypergrid/HypergridAdmin.cs:26`, `Pages/Hypergrid/Index.cshtml:58`
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/PeopleRules.cs` (`StaffFirst` use at line 40)
- Modify: `Pages/Advanced/_Saved.cshtml:10`, `Pages/Advanced/Regions.cshtml:11-19`
- Modify: `Pages/People/Delete.cshtml:44`, `Pages/People/Edit.cshtml:22`
- Test: `PeopleRulesTests.cs`, `HypergridPagesTests.cs`

- [ ] Hypergrid off:
  - `HypergridOff` becomes "Hypergrid is off, so links can't be added. Turn it on in Settings, Plan."
  - New `HypergridOffAskStaff` = "Hypergrid is off, so links can't be added. Ask the owner or an admin to turn it on." This is shown to supervisors.
  - Test: a supervisor sees the second text in `#hypergrid-off`, an admin sees the first.
- [ ] `PeopleRules`:
  - New `public const string OwnerChangesStaff = "Only the owner can change staff.";`.
  - When the target is staff, an Admin actor gets `OwnerChangesStaff` and the Owner keeps `StaffFirst`.
  - Test `Admin_on_staff_is_told_only_the_owner_can_change_staff`.
- [ ] Restart buttons get `class="btn btn-sm btn-primary ms-2"`.
- [ ] The two `p.error` become `div.error-box.error.alert.alert-danger role="alert"` (same text).
- [ ] Run `dotnet test Launcher/DreamGrid.Launcher.Tests` → all pass. Commit "Quick fixes from the menu, looks and workflows design".

### Task 2: The menu model

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Navigation/StaffMenu.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/StaffMenuTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record MenuItem(string Text, string Href, string ActivePrefix, bool Badge = false);
  public sealed record MenuGroup(string Text, string Href, IReadOnlyList<MenuItem> Items, bool Right = false)
  { public bool IsSingle => Items.Count <= 1; }
  public static class StaffMenu
  {
      public static IReadOnlyList<MenuGroup> For(StaffRole role, LauncherSettings s, bool canApprove);
      public static bool IsActive(MenuGroup g, string path);   // any item's ActivePrefix matches (Home: exact "/")
  }
  ```
  A single group renders as one link: `Items[0]`'s text and href, unless `Items[0].Href == g.Href` (the hub), in which case the group text is shown.

- [ ] Write tests, one `[Theory]` per row of spec §1 "What each person sees". Assert the top-level texts as rendered (single groups use the rule above):
  - Owner, Local: Home · World · People · Guest pages · Security · Settings · Test (the right-hand group's text is the person's name, passed in at render time; the model uses "You" as a placeholder `Text`, so assert "You").
  - Admin, Local: Home · World · People · Splash page · Settings · You.
  - Supervisor, Local, can't approve: People · Hypergrid · You. Can approve: People has items Find a person and Sign-ups.
  - Owner and Admin, RegionServer: Home · World · (Security for owner) · Settings · You. Owner, Connected: the same as owner RegionServer.
  - Items:
    - World: Regions `/regions`, Estates `/estates`, Hypergrid links `/hypergrid`, Region library `/regions/library`, Recently deleted `/regions/deleted`.
    - People: Find a person `/people`, Sign-ups `/people/sign-ups` (Badge), Groups `/groups`, Takedowns `/people/takedowns`, Staff `/staff`.
    - Guest pages: Splash page `/splash`, Legal pages `/security/legal`, Sign-up and bots `/security/bots`, Email server `/security/email`, Password reset `/advanced/password-reset`.
    - Settings: Plan, Network, Database, Region defaults (`/advanced/regions`), Logs `/logs`. Modules are not in the menu; they stay on the hub.
    - You: Sessions, Two-factor, Email notices (`/profile/emails`, staff plans only).
    - Hubs: World `/regions`, People `/people`, Guest pages `/guest-pages`, Settings `/advanced`, You `/profile/sessions`.
    - Who sees what follows the table in spec §1. Use `AdminRule.HasStaffPages(s)` for "staff plans".
  - `IsActive`: `/regions/region/x` → World, `/advanced/plan` → Settings, `/logs` → Settings, `/` → Home only.
- [ ] Run → fail (type missing). Implement. Run → pass. Commit.

### Task 3: Render the menu

**Files:**
- Modify: `Pages/Shared/_Layout.cshtml` (the `<nav>`), `wwwroot/css/site.css`
- Modify: `RegionsPageTests.cs:42-48` (the pinned order), `HypergridPagesTests.cs:200`
- Test: `LayoutTests.cs`

- [ ] Layout:
  - Keep the `enrolOnly` branch as is. Otherwise loop over `StaffMenu.For(role, settings, canApprove)`.
  - A single group renders as `<a class="nav-link">`.
  - Any other group renders as `<div class="nav-item dropdown">` containing `<a class="nav-link dropdown-toggle" href="{hub}" role="button" data-bs-toggle="dropdown" aria-expanded="false">` and `<ul class="dropdown-menu">` of `<a class="dropdown-item">`.
  - The right-hand group gets `ms-auto`, shows the signed-in person's name (`User.Identity.Name`), and its last item is the existing sign-out form as a `dropdown-item` button.
  - Badges:
    - The waiting badge (`N waiting`) shows on Home when Home shows, else on Sign-ups.
    - Only that first one has `id="signups-badge"`.
    - The supervisor-only badge link goes away.
- [ ] Tests in `LayoutTests`:
  - `Group_headings_link_to_their_hubs`: owner on `/profile/sessions`, `header nav a.dropdown-toggle` hrefs are `/regions`, `/people`, `/guest-pages`, `/advanced`, `/profile/sessions`.
  - `Sign_out_is_in_the_name_menu`: a form posting to `/logout` sits inside the last `.dropdown-menu`.
- [ ] Replace `Nav_has_regions_between_home_and_advanced` with `Nav_top_level_for_admin`. It asserts the `header nav > a.nav-link, header nav .dropdown-toggle` texts equal Home, World, People, Splash page, Settings, plus the admin's name.
- [ ] `HypergridPagesTests.The_menu_shows_Hypergrid_to_supervisors` keeps passing: for a supervisor, Hypergrid is a single `a.nav-link`.
- [ ] Run all tests and fix the menu-text expectations that moved. Commit.

### Task 4: Hubs, renames and back links

**Files:**
- Create: `Pages/GuestPages/Index.cshtml(.cs)` at `@page "/guest-pages"`
- Modify: `Program.cs` (add `/GuestPages` → `GuestSideOnly`)
- Modify:
  - `Pages/Advanced/*.cshtml` (Title and h1 "Settings"; breadcrumb text "Advanced Settings" → "Settings"; `Advanced/Regions` Title and h1 → "Region defaults"; Password reset leaves the Advanced hub)
  - `Pages/Security/Bots.cshtml` ("Sign-up and bots")
  - `Pages/Security/Email.cshtml` ("Email server")
  - `Pages/Profile/Emails.cshtml` ("Email notices")
  - `Pages/Security/Index.cshtml` ("Pages for guests" section → one line linking to Guest pages)
  - `Pages/Regions/Index.cshtml` and `Pages/People/Index.cshtml` (hub link rows)
- Test: `GuestPagesHubTests.cs`

- [ ] Tests:
  - Owner sees all five links on `/guest-pages`; admin sees only Splash page; supervisor is refused (not 200); RegionServer gives 404.
  - Legal, Bots, Email and Password reset each have `a[href='/guest-pages']` with text "‹ Guest pages" above the h1.
  - `/advanced` has no `/advanced/password-reset` link.
- [ ] Hub page: the same `list-group` markup as `Advanced/Index.cshtml`, with one line of help per item. Item visibility comes from `StaffMenu.For(...)`'s Guest pages group, so it can't drift from the menu.
- [ ] Regions hub: a small row of links under the h1, "Estates · Hypergrid links", shown when the menu would show them. People hub: "Sign-ups · Groups · Takedowns · Staff", the same way.
- [ ] Run all tests and update the title and heading expectations that changed. Commit.

### Task 5: Check on the VM
- [ ] Open a PR. When CI's `package-linux` is built, install it on the Debian 12 VM (see the VM notes). The owner signs in and looks at the menu as owner. The owner can make an admin and a supervisor to look as them.
