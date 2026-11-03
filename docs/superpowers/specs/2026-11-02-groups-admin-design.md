# Groups admin — design

Worklist item 2.8 (Wifi pages `groupslist`, `groupeditform`, `groupmembers-*`). Written 2026-11-02 with the owner.

## Why

The owner wants admins to manage the grid's groups from the website, and above all to keep **one default group that every resident is in**, so an admin can reach everyone with group chat and notices from the viewer. Today only the founder or officers can change a group, and only from inside the viewer; Wifi's add-on (`1DivaOpenSimServices`) offered rename, delete and remove-member, with no permission checks of its own.

The owner's rules:
- Admins can change groups, delete them and manage members.
- An admin who adds someone puts them in straight away — no invitation to accept.
- Being in a group is the resident's choice after that: they can leave in the viewer, or rejoin if the group is open to join. **The launcher never puts back someone who left.**
- No changes to OpenSim's own code.

## Goals

1. A **Groups** page listing every group.
2. A page per group: change the name and charter, see members and their roles, add a member, remove a member, delete the group.
3. A **default group**: one group, chosen by an admin, that new residents join as their account becomes active, plus an **Add everyone now** button for residents who already exist.
4. Each change is one database transaction and one audit-log line.

## Non-goals

- Creating groups, editing roles or powers, changing the insignia, fees or enrolment settings, sending notices, group chat. These stay in the viewer. (Owner agreed.)
- Hypergrid visitors' groups on other grids.
- Forcing membership: nothing re-adds a resident who left.

## Anti-goals

- A group change that leaves broken rows behind (a member with no Everyone role, an active group that no longer exists).
- Staff below admin seeing or changing groups.
- A resident being pulled back into the default group after they left it.

## Who can use it

Owner and admins only (the launcher's `AdminPolicy`). Supervisors and residents don't see the menu item or the pages. The pages are on the guest side like the other staff pages, under a new `Pages/Groups/` folder, which gets `AdminPolicy` from the folder rule. The menu item "Groups" sits with the other admin links.

## How it works

The launcher already reads and writes the grid's MariaDB `robust` database directly (`MySqlConnector`, as `StaffDirectory`, `OnlineUsers` and `AccountRemover` do). It works on OpenSim's core group tables (`os_groups_groups`, `os_groups_membership`, `os_groups_roles`, `os_groups_rolemembership`, `os_groups_invites`, `os_groups_notices`, `os_groups_principals`; schema in `OpenSim/Data/MySQL/Resources/os_groups_Store.migrations`). Rows are written the way OpenSim's own `GroupsService` writes them, so the viewer and the regions see an ordinary group.

Regions cache group data for about a minute (`RemoteConnectorCacheWrapper.GROUPS_CACHE_TIMEOUT = 60 s`), so a change reaches people already in-world within about a minute, or at their next login. The pages say so after each change.

If the database can't be reached (the grid is stopped), the pages show that groups are unavailable, the same way the Staff page does.

### Groups list

Columns: name, founder (avatar name), members, open to join (yes/no), and a "Default" mark on the default group. Sorted by name, with a name filter. *(Builder's call: paging if needed; grids here have tens of groups, not thousands.)*

### One group

- **Name and charter.** Saved together. The name must be 1–35 characters (the viewer's limit) and not already used by another group (the table's name is unique); the page says which rule failed.
- **Members.** Avatar name, their roles in this group, and whether they are a local resident or a hypergrid visitor (a member ID of the form `uuid;url;name`). Hypergrid members can be removed but not added.
- **Add a member.** By full avatar name, looked up the strict way the Staff page does (`AvatarName.Parse` then `FindAccountAsync`). Writes what OpenSim's `_AddAgentToGroup` writes for the Everyone role (`RoleID` = all zeros): a membership row (selected role Everyone, contribution 0, listed in profile, accepts notices) and an Everyone role-membership row. **It does not change the person's active group.** Removes any pending invitation of theirs to this group. Adding someone who is already a member does nothing and says so.
- **Remove a member.** Deletes their membership, every role-membership in this group and any invitation to it; if this group was their active group, their active group is cleared (all zeros). If they are the group's last member in its Owners role, the page asks for confirmation first, saying the group will have no owner left.
- **Delete the group.** Asks for confirmation, naming the group and its member count. Deletes, in one transaction: role-memberships, memberships, roles, invitations, notices, the group row, and clears it as anyone's active group. Notice attachments are inventory items owned by residents and are left alone. If it was the default group, the default is cleared.

### The default group

- On a group's page an admin can **make this the default group** (or stop it being the default). Only one group is the default at a time. It's stored in the same launcher state file as the "already added" list below, so choosing the default and recording its members is one write (and the settings schema doesn't change).
- **New residents** are added to the default group (Everyone role, active group unchanged) when their website sign-up becomes active: confirmed by email, approved by staff, or needing neither. A pending sign-up that is never approved is never added. If there is no default group, or it was deleted, nothing happens. A failure to add them is logged and never stops the sign-up.
  - Only these sign-up routes add someone automatically. Accounts made another way (the Robust console, Wifi while it is still loaded, the owner's own account at setup) are picked up the next time an admin presses **Add everyone now**.
  - A suspended account that staff restore is **not** added: that person isn't new, and they may have left the group before.
- **Add everyone now** puts every local resident whose account is active (user level 0 or above) into the default group, skipping:
  - people already in it;
  - anyone the launcher has added to this group before (they may have left);
  - hypergrid visitors (they have no local account).
  It reports how many were added and how many were skipped, and writes one audit line with those counts.
- **Remembering who was added.** The launcher keeps, per group, the IDs of everyone who has been in it as far as the launcher knows. That list lives in a launcher state file (`JsonStateFile`, e.g. `guests/group-joins.json`). It is what lets the button skip people who left. The list gets someone when:
  - a group is **made the default**: all its current members are recorded straight away, so a member who leaves before the button is pressed is never pulled back;
  - the button finds them already in the group;
  - the launcher adds them by any route (the button, a new resident, an admin adding them by hand).
  Nobody is ever taken off the list, including people an admin removes on the website. An admin can still add a person by hand, even one who left: that is a deliberate choice, not an automatic one.
- **Order of writes.** The list and the group tables can't share one transaction, so the launcher writes the person to the list **first**, then adds them to the group. If the database step fails, they stay on the list and the button will skip them later: the failure goes towards leaving someone out (an admin can add them by hand), never towards pulling back someone who left. If writing the list fails, nothing is added.
- Changing the default group changes who new residents join from then on; it doesn't move anyone.

### Audit log

One line per action through `AuditLog.Write`, with the admin's name, ID, role and IP. Actions: `group-renamed` (target: group ID and new name; the charter change is logged as `group-charter-changed` without its text), `group-member-added`, `group-member-removed`, `group-deleted`, `group-default-set`, `group-default-cleared`, `group-add-everyone` (with counts), and `group-auto-joined` for a new resident (actor: the launcher).

## Constraints

- No changes to OpenSim code or its config; `1DivaOpenSimServices` stays until Phase 3 removes it (worklist 3).
- .NET 10, ASP.NET Core Razor Pages, `MySqlConnector`; SQL with parameters only.
- The site's content security policy holds: no inline scripts or styles (`InlineScriptScanTests`).
- Every post carries the anti-forgery token, like the other staff forms.
- English only until worklist 1.5.

## Decided with the owner

- Admins add people directly, no invitation.
- A default group for messaging that everyone is in, new residents included.
- Leaving is the resident's choice; the launcher never puts them back.
- Creating groups, roles and notices stay in the viewer.

## My calls (owner to check)

- New residents join when their sign-up becomes **active**, not when it is first submitted. Accounts made outside website sign-up wait for the button; restored suspended accounts aren't added.
- Making a group the default records its current members at once; nobody is ever taken off the "already added" list.
- The list is written before the group, so a half-finished add leaves a person out rather than forcing them back in.
- Adding someone never changes their active group (OpenSim's own join does).
- The "already added" list also covers people added by hand, so the button never re-adds them either.
- Removing the last owner is allowed after a warning.
- The owner's own account and existing staff are treated like any resident (they're added by the button if they're active).

## Left to the builder

Page layout and wording, the list filter, how the SQL is organised, and test structure.
