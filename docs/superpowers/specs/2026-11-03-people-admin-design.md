# People admin (worklist 2.5a) — design

Date: 2026-11-03. Worklist item 2.5, part a. Parts b (inventory takedown) and c (history for legal requests) get their own designs.

## Why

On a public grid, the owner has to look after the people on it. Today the People page finds one avatar only by their exact full name, and the only thing staff can do there is suspend. Everyday requests can't be handled from the website: a resident asking for a new name, a changed email address, a hijacked account, or someone who has to go. The owner said: "on public grids protection is an owners requirement."

## Goals

- Find people without knowing their exact name: search by part of a name, filter, and page through.
- Handle the common tasks from the list itself, with one icon each: edit, send a reset link, sign out everywhere, suspend or unsuspend, delete.
- Each person has their own page with the same tasks and their details.
- Every task is checked against the same who-may-do-what rules the site has today, and is written to the audit log.

## Non-goals

- Staff roles. Making someone an admin or supervisor stays on the Staff page.
- A free-form level field. The grid only uses resident, supervisor, admin and the owner. Hypergrid links (2.9) belong to the owner, admins and supervisors, so no level 50 is needed (owner, 2026-11-03).
- Typing or showing anyone's password. Staff send a reset link instead.
- Inventory, takedowns and history exports (2.5b, 2.5c).
- Bulk actions.

## Anti-goals

- Deleting someone by accident, or deleting evidence by accident. That is why delete only works on a suspended person and always asks first.
- Staff able to take over an account quietly. Any change to a person's email or name tells them.
- A page that leaks the user table to anyone who isn't staff.

## Decisions by the owner

- **The list (option A, with icons).** `/people` becomes a searchable, paged list. Each row ends with task icons. More icons can be added later.
- **The five tasks:** edit, send reset link, sign out everywhere, suspend / unsuspend, delete.
- **Delete is delete; suspend is suspend.** Delete removes the person at once and for good. There is no "disabled, purge later" step.
- **Suspend before delete.** The delete icon works only on someone who is already suspended.
- **Delete asks "Are you sure?"** before anything happens.
- **Name changes are done on the resident's request.** The edit box covers first and last name and email.
- **A staff-sent reset link resets two-factor.**

## The list: `/people`

- A search box (part of a first or last name, or a full "First Last") and a filter: Everyone, Staff, Suspended, Waiting for approval. Results are sorted by last name, then first name. 25 per page, with Previous and Next.
- Columns: Name (links to the person's page), Role, Email, Last seen, Tasks.
- Badges next to the name: Owner, Admin, Supervisor, Suspended, Waiting for approval / Waiting for email (as today).
- Last seen comes from Robust's `GridUser` login and logout times. "Online now" when they are in-world (Presence), "never" when they have no login.
- Hypergrid visitors are not in `UserAccounts`, so they don't appear. That is fine for this page.
- An empty search with the Everyone filter shows the first page of everyone. The query is always limited and paged, so a large grid never loads every row.
- Waiting sign-ups still link to `/people/sign-ups` for approve and deny.

## Task icons

Left to right. Each icon is a button with a visible tooltip and an accessible name. An icon that can't be used on that person is shown greyed and disabled, and its tooltip says why. The server rechecks every rule on submit, whatever the page showed.

| Icon | Task | Allowed |
|---|---|---|
| Pencil | Edit name and email | Owner and admins, on residents and suspended residents. |
| Key | Send a password reset link | Owner and admins, on residents with an email address who aren't suspended. Staff and the owner use Forgot password themselves. Greyed: "No email address", "Suspended", "Staff use Forgot password". |
| Door | Sign out everywhere | Owner and admins, on residents and suspended residents. |
| Pause / play | Suspend / unsuspend | As today (`StaffChanges`): the owner on anyone but themself; admins on supervisors and residents; supervisors on residents. |
| Bin | Delete | Owner and admins, on suspended residents only. Greyed: "Suspend them first". |

Common to every icon:
- Nobody uses these on their own account. They use their own account page.
- The owner can't be changed here.
- Admins and supervisors can't be edited, signed out or deleted from here. Make them a resident on the Staff page first.
- Supervisors keep only what they have today: looking people up and suspending or unsuspending residents. Their other icons are greyed.

## The person's page: `/people/{id}`

- Their details: name, role, level, email, created, last seen, online now (and in which region), region rights, and estates they own.
- The same task buttons, with words next to the icons.
- Their recent audit entries as a target (the last 20), so staff can see what was done to the account before.

## Edit

A form with first name, last name and email. Only what changed is saved, and each change is audited with the old and new value.

### Name
- The same rules as sign-up (`SignUpRules.NameProblem`): each part is 1–31 letters, digits or `-`, and the name must not be taken or reserved.
- The person must be offline. The page says so and offers Sign out everywhere first. The launcher writes the name to `robust.UserAccounts` in one statement, the way `RobustEmails` writes email.
- Everything else is tied to the account, so friends, groups, inventory and regions carry over. The page warns that viewers may show the old name until they refresh, and that other grids they visited by hypergrid may show it for longer.
- The old name is held for 30 days, so nobody else can take it and pose as them. Held names count as taken at sign-up and in this edit box.
- If they have an email address, they get a short notice that their name was changed by staff.

### Email
- The same rules as sign-up (`SignUpRules`): at most 64 characters, compared trimmed and lower-case, and not in use by another account.
- Staff set it directly. Staff are acting on the person's request, so no confirmation link is sent. The old address gets a notice that staff changed it. It reuses the wording of `EmailChangeNotice`, with a line saying staff made the change. The new address gets a short welcome notice.
- Clearing the address is allowed. The old address still gets the notice.

## Send reset link

- It sends a single-use, 30-minute reset link to the address on the account, like Forgot password does, but marked as sent by staff.
- **A staff-sent link resets two-factor too** (owner, 2026-11-03). This is for the hijacked account: staff put the right email back and send the link. The person doesn't need the two-factor code, and when they set the new password, two-factor is turned off for them. They can turn it on again from their account page. The email says so.
- Two-factor is cleared only when the link is used, not when it is sent. An unused link changes nothing.
- A staff-sent link works even when the owner's recovery rule is Off for residents. *(My call: it is a deliberate staff act, not self-service.)*
- Using it ends every session, as a normal reset does. It is audited as `reset-used` with "two-factor cleared" when it was on.
- The staff send skips the per-IP limit but keeps the per-account limit (3 an hour), so a busy admin can't flood someone's inbox.
- It is greyed when email isn't set up, when the account has no address, when the person is suspended, or when they are staff.
- Audited as `reset-sent`.

## Sign out everywhere

- It ends all their website sessions: `GuestStamps.Rotate` for the resident side, and `StaffSessions.EndAll` if they ever had staff sessions.
- If they are in-world, it logs them out of the grid as well: the launcher asks the region they are in (from Presence) to kick them, with a short message.
- Audited as `signed-out`.

## Suspend / unsuspend

Unchanged from today (`StaffActions`, level −1, the old level kept in `SuspendedStore`), now reached from the list and the person's page. Suspending also signs them out everywhere, as above, so a suspended avatar is not left in-world.

## Delete

- Only for a suspended resident.
- First an "Are you sure?" page. It names the person and lists what goes:
  - the account and password
  - inventory
  - friends (their side and others' entries for them)
  - group memberships and roles
  - profile, picks, classifieds and notes
  - offline messages
  - region rights, and their estates (regions go back to their home estate)
  - the held-name record
- Things they made that live as assets (textures, objects others own copies of) stay.
- **Their estates go back to the default** (owner, 2026-11-03). Delete removes their region rights, and every region still in one of their estates goes back to its home estate (`RegionRights.HomeEstateOf`), the same move `RegionEstateSync` makes when a right is removed. Their estate itself (the "First Last Estate" it made), now empty, is removed, with its managers and bans. The "Are you sure?" page lists the regions that will move.
- So owning a region doesn't block delete. *(My reading of "go to the default status"; a region is never left owned by a removed account.)*
- If a region can't be moved (for example it is stopped and the move has to wait), delete waits for the move and says so, rather than leaving a region in a removed person's estate.
- On confirm, everything is removed in one database transaction where the tables share a database. The launcher's own files (region rights, stamps, suspended store) are updated afterwards. If anything fails, the page says what was and wasn't removed.
- Audited as `deleted`, with the name. After this the audit log is the only trace.
- 2.5b and 2.5c will add a **hold**. A held account can't be deleted. Delete should check through one place that can later refuse a held account.

## Security

- All of `/people` stays behind `StaffPolicy`, and the `StaffPagesOnly` and admin-network rules apply, as today. Task posts use antiforgery tokens, never GET.
- The CSP stays as it is. Icons are a local SVG sprite under `wwwroot`, with no outside icon service and no inline script.
- Search text is used only as a bound SQL `LIKE` value, with `%` and `_` escaped, and with a length limit.
- Audit entries use `LogText.Clean` for names, and never contain passwords, tokens or email bodies.

## Left to the builder

- Page and handler layout, the edit form's shape, and how the "Are you sure?" step is drawn. A separate page like `/people/sign-ups/deny` fits the site's no-script style.
- How Last seen and the filters are queried: direct SQL on `robust` like `StaffDirectory`, a new reader class, and indexes if needed.
- The exact table list for delete. Check every table that refers to an avatar in the `robust` database and the region (`opensim`) database (for example estate managers and bans), and say in the plan which are cleaned up and which are left.
- How the in-world kick is sent: the region console `kick user`, through the existing REST console, is the expected route.
- Whether a name change also needs Robust's or the regions' name caches refreshed, beyond what logging in again does.
- Tests: rules (who can do what, greyed reasons), search escaping and paging, the delete table list against a test database, and the email notices.
- The owner's check on the VM, in Firestorm:
  - search and page
  - rename someone and see the new name after they log in again
  - change an email and see both notices
  - send a reset link and use it
  - sign out someone who is in-world
  - suspend, then delete, and see they can't log in and their name is free
