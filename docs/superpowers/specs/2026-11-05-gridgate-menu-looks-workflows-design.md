# GridGate menu, looks and workflows — design

The staff pages of GridGate (the launcher website) were added one feature at a time: Regions, Advanced, Logs, Splash page, Groups, People, takedowns, Staff, Security, the guest side, Hypergrid and Estates. Each page works, but the menu has grown into one long row, the pages look slightly different from each other, and similar tasks work in different ways. This design makes all three the same everywhere. It adds no new features.

The owner agreed with the plan on 2026-11-05 ("Looks right, write it"). The owner didn't answer the four open questions, so the answers below are marked **(my call)** for the owner to check.

Reviewed against V7 at `25268eb`. File references are under `Launcher/DreamGrid.Launcher/` unless they say otherwise.

Legend, as in the work list: **[S]** security requirement, **[X]** cross-platform note, **[?]** decision still open.

## Why

- **The menu doesn't fit.** The owner gets 15 links in one row, which wraps onto two or three lines on a laptop. Your own pages (Sessions, Emails, Two-factor) sit beside the grid's pages. "Emails" (your notices, `/profile/emails`) and "Email" (the mail server, `/security/email`) are easy to mix up.
- **Pages look different from each other.**
  - "Done" messages are blue on the Regions pages and green everywhere else.
  - Back links come in about seven styles.
  - "Database down" has four wordings and three styles.
  - Two pages show errors as bare text (`People/Delete.cshtml:44`, `People/Edit.cshtml:22`).
  - The "Restart your world" button has no button style (`Advanced/_Saved.cshtml:10`).
- **[S] Confirmations are uneven.** Some deletes ask again. Other changes to someone's access happen on one click: making someone a guest, resetting two-factor, removing an estate manager.
- **Workflows hit dead ends.**
  - Names on group, estate, staff and sign-up lists never link to the person.
  - The person page doesn't show their groups and doesn't link to their regions or estate.
  - Admins are told "Change staff on the Staff page first", but only the owner can open Staff (`Core/Security/PeopleRules.cs:24`).
  - "Hypergrid is off" points to Advanced → Network, but the switch is on Advanced → Plan (`Hypergrid/HypergridAdmin.cs:26`).

## Goals
- One menu that fits on one line on a laptop and shows each person only what they can open.
- One way to show each thing: a heading, a back link, a "done" message, an error, an empty list, "database down", a table, a confirmation.
- Every name of a person, region, estate or group links to its page.
- Similar tasks work the same way.

## Not in this design
- New features: a list of all jobs, IAR save and load, searching Estates, Staff or Regions. These stay on the work list.
- Changing who may open what. The access rules in `Program.cs` (folder policies, `GuestSideOnly`, `StaffPagesOnly`) stay exactly as they are. Only the menu and links change.
- The setup pages (`Pages/Setup`) and the guest pages' looks. The guest side keeps its own simple layout. It has no menu, by design.
- Languages. English only until work list 1.5. New text is written so it can be translated later: whole sentences, with no words glued together in code.

## Things it must not do
- No inline scripts or styles (CSP). The menu groups use Bootstrap's dropdown from `bootstrap.bundle.min.js`, which is already loaded, and classes in `site.css`.
- **[S]** The menu must never be the only guard. A link that is hidden doesn't make a page safe. Every page keeps its own access rule.
- **[X]** It must look and work the same on Windows and Debian. Nothing in it depends on the system.
- It must still work without scripts: each group heading is also a link to a hub page.

## 1. The menu

### Groups

| Top level | Inside | Who sees it | Plans |
|---|---|---|---|
| **Home** (with the "N waiting" badge) | — | owner, admins | all |
| **World** | Regions · Estates · Hypergrid links · OAR library · Recently deleted | Regions, OAR library, Recently deleted: owner and admins. Estates: owner and admins, staff plans only. Hypergrid links: all staff, staff plans only | all |
| **People** | Find a person · Sign-ups (with badge) · Groups · Takedowns · Staff | Find a person: all staff. Sign-ups: whoever may approve (`ApprovalRules.CanApprove`). Groups, Takedowns: owner and admins. Staff: owner | staff plans |
| **Guest pages** | Splash page · Legal pages · Sign-up and bots · Email server · Password reset | Splash page: owner and admins. The rest: owner | staff plans |
| **Security** | — (one link to `/security`) | owner | all |
| **Settings** (today's Advanced) | Plan · Network · Database · Region defaults · Modules · Logs | owner and admins | all |
| **Your name** (at the right) | Sessions · Two-factor · Email notices · Sign out | all staff. Email notices only on staff plans | all |

"Staff plans" means `AdminRule.HasStaffPages`: Local, SimplePublic and GridAsset. They're the only plans that have a guest side and supervisors.

### Rules for the menu
- A group with nothing in it the person can open is not shown. A group with one item shows as a plain link with that item's name, except when that item is the group's own hub page. Then the group's name is kept.
  - A supervisor's World group holds only Hypergrid links, so it shows as **Hypergrid links**.
  - A supervisor who can't approve sign-ups has only "Find a person" in People, which is the People hub, so it shows as **People**. That is the default, because sign-up approvers are owner and admins unless the owner changes it on the Sign-up and bots page.
- Each group heading links to a hub page. Without scripts, a click opens the hub. With scripts, it opens the dropdown. Hubs:
  - World → `/regions`. It already links to Library and Recently deleted, and gets links to Estates and Hypergrid.
  - People → `/people`. It gets a short row of links to Sign-ups, Groups, Takedowns and Staff, for the people who can open them.
  - Guest pages → a new `/guest-pages` hub page, in the style of `Advanced/Index.cshtml`'s list. It is admin policy plus `GuestSideOnly`, and shows each item only to those who can open it.
  - Settings → `/advanced`.
  - Your name → `/profile/sessions`.
- The group whose page you're on is marked active, as today.
- Someone signed in only to set up two-factor still sees just Two-factor and Sign out (`TwoFactorGate.MustEnrol`).
- The waiting badge works as it does today: on Home for owner and admins, and on Sign-ups inside People. A supervisor's separate badge link goes away, because Sign-ups is in their People group.

### What each person sees

| | Owner | Admin | Supervisor |
|---|---|---|---|
| Local, Simple Public, Grid Asset | Home · World · People · Guest pages · Security · Settings · Your name | Home · World · People · Splash page · Settings · Your name | Hypergrid links · People · Your name |
| Region Server | Home · World · Security · Settings · Your name | Home · World · Settings · Your name | (no supervisors) |
| Connected | Home · World · Security · Settings · Your name | (no admins) | (no supervisors) |

### Names and moves
- "Advanced" is renamed **Settings** in the menu, its heading and its tab title **(my call)**. The `/advanced` addresses stay, so links and bookmarks still work.
- The group holding Regions, Estates and Hypergrid is called **World**, matching "Your world" on Home **(my call)**.
- **Logs** moves into Settings and is no longer a top-level link **(my call)**. It stays at `/logs`.
- **Password reset** moves from Advanced into Guest pages. It is owner-only and guest-side, and needs the email server first. Its page stays at `/advanced/password-reset`, which is the least change. Its back link goes to Guest pages.
- "Who can approve new sign-ups" stays on the Bots page, but that page is named **Sign-up and bots**.
- **Security** (`/security`) keeps two-factor sign-in, the allowed networks and the two-factor lists. Its "Pages for guests" section becomes one line linking to Guest pages. Legal pages, Sign-up and bots, Email server and Password reset all get the back link "‹ Guest pages" **(my call)**.
- Profile "Emails" is named **Email notices**. Security "Email" is named **Email server**.
- A few page titles change to match their headings, and "Regions" stops being the title of two pages: Advanced/Regions becomes **Region defaults**.

### Tests
`RegionsPageTests.Nav_has_regions_between_home_and_advanced` pins today's order. It is replaced with one test per role and plan from the table above, checking the top-level names and what's inside each group. One more test shows that each group heading links to its hub.

## 2. How pages look

The de-facto house style (what most pages already do) becomes the rule. Each rule below gets one shared partial or one CSS class, so pages can't drift again.

1. **Page top.** In this order:
   - A back link `‹ Parent` above the heading on every page that isn't a menu item. It replaces breadcrumbs, "All groups", "Back to …" at the top or bottom, and Back buttons.
   - One `<h1>` whose text is the page's tab title (`ViewData["Title"]`). A person or thing's page uses its name (for example "Sunny Bay").
   - One plain `<p>` intro, if the page needs one. It comes before any message.
   - Partial: `_PageTop` (back link, heading, intro).
   - The region page gets a real `<h1>` when opened as a full page. Deleted and Job use the normal `<h1>` size.
2. **Messages.** One partial `_Notice` with three kinds:
   - **Done**: green `alert-success`, `role="status"`. Every "done" message, including all the Regions pages and the dashboard.
   - **Error**: red `alert-danger`, `role="alert"`, at the top of the page, plus the field's own error under the field when a field is wrong. No bare red text, no unstyled `p.error`.
   - **Check this**: yellow `alert-warning`. For "are you sure" boxes and things to know before acting.
   - Blue `alert-info` is only for neutral information. It is never used for "done".
   - Wording: say what happened in a full sentence with the name, for example "Ada Lovelace is now a manager." Not "Saved successfully."
3. **"Database down" and "world not running".** One partial `_WorldDown`, in the yellow style, with one wording: "Your world isn't running, so this can't be shown. Start it on Home." The link goes to Home for those who can open it. A supervisor's version says "Ask the owner or an admin to start the world." The four wordings in the code become constants in one place.
4. **Empty lists.** Grey text "No … yet.", followed by what to do next if there is something, for example "Add one below." An empty table is never shown on its own: the Staff table and group members get the message too.
5. **Tables.** `.table-responsive > table.table.align-middle` with a `thead` everywhere. No `table-sm` on the staff pages. The name in the first cell links to its page. Action buttons sit in the last cell as small outline buttons.
6. **Forms.**
   - Stacked labels and full-width boxes, except short boxes (numbers, X and Y, Size), which use `w-auto`.
   - X and Y boxes are laid out the same way on every page.
   - Help text uses `form-text` only. `.hint` goes, after a check that no test relies on it.
7. **Buttons.**
   - One filled main button per form, then `Cancel` as an outline button.
   - Words are verbs:
     - Save, Add, Remove, Delete, Search. "Find" on Groups becomes Search.
     - Longer verbs where the short one would be unclear, for example "Move here".
   - "Yes, …" buttons are red for removing or deleting.
8. **Cards.** Only the Home dashboard uses cards. Regions, Library, Database and Sign-ups become flat pages with `h2` sections, like the rest **(my call: it was the smaller change)**.
9. **Spacing.** Sections are `h2` with the space above them that `site.css` gives every `h2`, so pages don't add their own `mt-4`.

## 3. Confirmations [S]

Three levels, chosen by what can't be undone. Pop-up `confirm()` boxes (`data-confirm`, `confirm.js`, the copy in `map.js`) go away, because they vanish without scripts and look different from everything else.

| Level | When | How | Examples |
|---|---|---|---|
| **One press** | Easy to undo, or affects only you or a link | The button acts and shows a "done" message | End a session · suspend or unsuspend · remove a hypergrid link · close a takedown case · remove a file from the library list · "Show passwords" on Database (it deletes nothing) · Start and Stop on Home (starting again undoes it) |
| **Ask again** | Changes someone's access | The server shows a yellow box on the same page: "Remove Ada Lovelace as a manager of Sunny Bay?" with a red "Yes, remove" and Cancel. It is a partial `_ConfirmBox` | Remove a group member or estate manager · make someone a guest · reset someone's two-factor · revoke a region owner · reset email wording to default · remove an email account |
| **Tick, then red button** | Can't be brought back | A box to tick that says what will be lost, and a red button. The post is refused if the box isn't ticked. It's either on the page or on its own confirm page when there is a lot to explain | Delete a person · delete a group or estate · erase a region · a move or import that overwrites land |

- Today's server "ask again" forms (Deleted, Library, Region, Import, Settings basics) move onto `_ConfirmBox`. The Group "remove member" form gets its missing question text.
- **[S]** Every level is checked on the post, not only shown. A "Yes" post without the ask (level 2) or without the tick (level 3) is refused. The audit log is unchanged.
- **Only the website's own forms change.** The `/api/*` endpoints, the port-8001 command contract on `/` (`LegacyRootPolicy`) and the command line keep working in one call, as today. Start and Stop are one press, so nothing there needs an ask **(my call)**.
- **The splash editor keeps its in-page prompts.** Discard draft, delete a block and remove the logo are script-only buttons inside an editor that needs scripts anyway, and nothing is saved until Publish. Those three stay as they are **(my call)**.
- **In the map panel**, a region-page post that needs an ask or a tick opens the full region page with the box shown there, instead of asking inside the panel. Posts that need no ask stay in the panel, as today. That way `map.js` doesn't need its own `data-confirm` copy **(my call)**.

## 4. After saving [S]

Every form post that changes something redirects back to its page (post, redirect, get) and shows the "done" message from TempData, so refreshing never sends the form again. Today Hypergrid, Staff, Security, People, the Advanced pages, Group edits and Estate edits return the page straight from the post. Errors still show on the same page with what was typed, without a redirect.

## 5. Workflows

1. **Names link everywhere:**
   - People link to their person page wherever they appear: Staff, group members and founder, estate owner and managers, region owner, sign-up rows, the Security two-factor lists and Who's online. This applies only for those who can open People.
   - Regions link to the region page; estates and groups link to theirs.
2. **The person page:**
   - It shows their groups (linked), their estate (linked) and their regions (linked).
   - For the owner, it has a "Make staff" link that opens Staff with the name filled in.
   - For an admin looking at a staff member, it says "Only the owner can change staff." instead of pointing at the Staff page.
3. **Sign-ups:**
   - A waiting person's page has Approve and Deny for whoever may approve. These use the same handlers and rules as the Sign-ups page.
   - The disabled icons on the People list link to Sign-ups.
   - After Approve or Deny, the Sign-ups list keeps its search, sort and page. Deny's Cancel goes back to where you came from.
4. **Regions and estates:**
   - The region page shows its estate (linked) on staff plans. Next to it is one line: "To move it, open the estate it should join and use Move a region here", linked to Estates. No new move form is added **(my call)**.
   - Picking an estate on the Add region form is **not** in this design. It needs a new kind of pending move, because today's moves only go to the owner's estate or back to the grid's. It goes on the work list as its own item **(my call)**.
   - The full region page gets "‹ Regions".
   - Both Home and the Regions list open the full region page.
   - A finished Add links to the new region's page as well as the map.
   - A region's Settings links to Region defaults.
5. **Jobs.** The Job page shows the last finished job for that region instead of a 404, until the next job starts. Jobs stay in memory only; they don't survive a restart. It gets "‹ region name".
6. **Takedowns.**
   - The new-case page gets a "Sign them out" button. It uses the person page's existing task, which needs the takedown access.
   - The person page's takedown counts link to that person's cases.
   - The Takedowns list says where to start: "Open a person's inventory to start a case", with a link to Find a person.
7. **Hypergrid.** The "off" message says "Turn it on in Settings, Plan." For supervisors it says "Ask the owner or an admin to turn it on."
8. **[S]** **The leftover `/people?name=` page goes.** It is the old "Found" card with its own Suspend handlers in `People/Index.cshtml.cs` (`LookUpAsync`, `Found`). Nothing links to it, and it duplicates the person page. The address redirects to the search (`/people?q=…`). Removing it means one less place where suspend rules are checked.

## The steps (one PR each, in this order)

1. **Quick fixes.**
   - The Hypergrid "off" message (§5.7) and the admin staff message (§5.2, last part).
   - The Restart button style, and the two unstyled errors.
   - Remove `/people?name=` (§5.8).
   - Small, and every item is safe to ship alone.
2. **The menu** (§1). Grouped menu, the Your name menu, the Guest pages hub and its back links, the renames and moves, the menu tests.
3. **Shared page parts** (§2): `_PageTop`, `_Notice`, `_WorldDown`, `_ConfirmBox`, the CSS, then applied to every page. This can be split in two PRs if it's large: first the People side (People, Groups, Estates, Hypergrid, Staff, Security, Profile), then the Regions side (Regions, Settings, Logs, Splash, Home).
4. **Confirmations and redirect after saving** (§3, §4). They touch the same handlers, so they go in one PR. Each level gets a test that a post without the ask or tick is refused.
5. **Linking things up** (§5.1–5.6).

## Left to the builder
- How the dropdown is marked up, as long as it follows the rules above and Bootstrap's accessible dropdown pattern (keyboard, `aria-expanded`).
- Exact wording of each "done" message and empty state, following the wording rule in §2.2.
- Whether level-3 confirmations sit on the page or on a confirm page, case by case. Delete a person keeps its confirm page.
- Splitting step 3 into two PRs or not.

## Decisions still open
- **[?]** Renaming Advanced to Settings, calling the group World, and moving Logs into Settings are **(my call)**. The owner can change any of them before step 2 starts.
- **[?]** Picking an estate when adding a region: left out (§5.4), to be added to the work list if the owner wants it.
