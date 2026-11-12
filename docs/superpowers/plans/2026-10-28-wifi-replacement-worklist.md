# Work list — replace Wifi with GridGate, add languages, remove Wifi

**GridGate** is the name of the launcher website (v7), the replacement for the Diva Wifi add-on (owner, 2026-11-03). Use it for the new site in docs and UI text; "Wifi" and "Diva" mean only the old add-on.

Goal: GridGate takes over what the Diva Wifi add-on did. Each feature is redesigned with security first, not copied. The site supports more than one language. Afterwards Wifi is removed from the tree completely. Everything works on Windows and on Debian 12 and 13.

This list is the backlog. Each phase becomes its own plan (`docs/superpowers/plans/…`) before work starts.

Legend: **[S]** security requirement, **[X]** cross-platform note, **[?]** decision still open.

---

## Phase 0 — Clean up now (before anything else)

- [ ] **[S]** `bin/config-addon-robust/Wifi.ini`, `bin/config-addon-opensim/Wifi.ini` and `bin/Wifi.ini` are tracked in git with real secrets (a Gmail SMTP app password, the Wifi admin password, MySQL credentials).
  - [ ] Revoke the Gmail app password and change the admin password (the owner does this).
  - [x] Replace the values with placeholders and stop tracking the live files; keep only `.proto` files in git. (Plan 5.)
  - Decided: git history is **not** rewritten. The old values stay in history, so revoking them is what protects the grid.
- [x] Add a secret scan (for example gitleaks) to CI so this can't happen again. (Plan 5: `secret-scan` job.)

## Phase 1 — Foundations every later feature needs

> **Was on hold (owner, 2026-10-29)** until install and owner plans were designed and built: `docs/superpowers/specs/2026-10-29-install-and-plans-design.md`. 1.1 HTTPS moves to the future public WAN website; its draft is kept as notes in `docs/superpowers/specs/2026-10-29-https-design.md`.

> 6a done; 6b Region Server done; 6c Connected Server done. Phase 1 (HTTPS) is no longer on hold for the plans work.

### 1.1 HTTPS
- [ ] **[S]** Serve the website over HTTPS. Options: a certificate the user supplies (PEM or PFX), Let's Encrypt (ACME), or a self-signed certificate made on first run for a LAN.
- [ ] **[S]** Once HTTPS is on: mark the cookies `Secure`, redirect HTTP to HTTPS, send HSTS.
- [ ] **[X]** Store the certificate in the owner-only data folder on both systems. On Debian, ACME on port 80 needs `CAP_NET_BIND_SERVICE` in the systemd unit; on Windows, the firewall rule goes in the installer.
- [ ] Document running behind a reverse proxy (Caddy or nginx), with forwarded-headers support turned on only for a configured proxy address.

### 1.2 Two audiences: admins and residents
- [x] (staff side done in plan 7a; resident area done in plan 7b: `/guest/sign-in`, `dg-guest` cookie, front page, legal pages, Terms acceptance, My regions) Split the site into an **admin area** (avatar level 200 or higher, today's `dg-auth`) and a **resident area** (any confirmed avatar). Use a separate cookie and scheme so a resident session can never reach admin endpoints.
- [x] Roles taken from the Robust `UserLevel`: resident (0+), privileged (for example 50, used for hypergrid links), admin (200+). Make the levels settings, not magic numbers. (admin and supervisor levels as settings; the privileged level 50 for hypergrid is item 2.9, not built in 7a)
- [x] More than one admin: list who has admin level, promote and demote, and refuse to remove the last admin. (The last admin can't be removed: the owner is always an admin and can't be changed.)
- Decided: the resident area is on the **same site and port** as the admin area. Still worth adding: an optional "admin pages only from these networks" allow-list.

### 1.3 Security baseline
- [x] **[S]** Login throttling per avatar name **and** per client IP. Keep the lock state across restarts (store it in the data folder).
- [x] **[S]** Security headers: Content-Security-Policy (no inline script), `X-Content-Type-Options`, `Referrer-Policy`, `frame-ancestors 'none'`.
- [x] **[S]** Audit log for admin actions (who, what, when, from where) in its own file under the logs folder, with no passwords or tokens in it.
- [x] **[S]** One shared helper for "tokens sent by email": random 256-bit value, only its hash stored, single use, expiry, bound to its purpose.
- [ ] **[S]** Bot protection for public forms (sign-up, forgot password). Built and switchable in plan 7b; it guards the real sign-up and forgot-password forms once they exist (2.1, 2.3). Every layer has its own on/off switch on an admin settings page:
  - [x] A1 honeypot field — **on** by default.
  - [x] A2 rate limit per IP — **on** by default, with an adjustable limit (for example sign-ups per hour).
  - [x] B proof-of-work puzzle (ALTCHA, self-hosted) — **on** by default, with an adjustable difficulty.
  - [x] C self-hosted image CAPTCHA — **off** by default; with a text alternative for accessibility (a sum in words). Localisation is 1.5; an audio alternative is on the later list.
  - [x] Admin approval of new accounts — a switch, **off** by default (2.1 and 2.2). (Plan 11: on the Bots page as "When email isn't set up, new sign-ups wait for staff approval", with "Who can approve new sign-ups".)
  - [x] **[S]** Turning a layer off is written to the audit log. The settings page warns when every layer is off and sign-up is open.
- [x] **[S]** Optional TOTP two-factor sign-in for admins. The Robust password stays the first factor.
- [x] Sessions page: an admin sees their active sessions and can end all of them ("sign out everywhere" rotates a per-user stamp).
- [x] Checked the legacy port-8001 routes: `/dreamgrid/regionready` accepts only loopback (tested); the command API on `/` stays reachable from the network on purpose (MachineId or an admin avatar), obeys the admin network list and the throttle, refuses a password alone for two-factor accounts, and audits commands that ran (design 2026-10-30 §7.8).

### 1.4 Email
> Done in plan 8a (`docs/superpowers/plans/2026-10-31-plan8a-email.md`, design `docs/superpowers/specs/2026-10-31-email-design.md`): provider cards, several accounts in order with their limits, an encrypted outbox. Office 365 is on the later list.
- [x] SMTP settings page: host, port, TLS mode, user, password, sender address, and a "send test email" button. (`/security/email`: provider cards and Other server.)
- [x] **[S]** Store the SMTP password encrypted with data protection. It is never shown back in a page (write-only field).
- [x] **[X]** Use MailKit (works the same on Windows and Debian) rather than `System.Net.Mail`.
- [x] Email templates are localised (see 1.5), in plain text and HTML. (Plain text and HTML done, editable at `/security/email/wording`; built in for en, de, es, fr, pt and ru by plan 16a; plan 16b's language selector edits, previews and test-sends each email in each language, added languages included.)
- [x] Every feature that needs email is hidden or explains itself when SMTP isn't set up. (`EmailSender.IsReady`; the features in 2.1–2.3 use it.)

### 1.5 Languages (localisation)
> Built in plan 16a (`docs/superpowers/plans/2026-11-10-plan16a-languages.md`, design `docs/superpowers/specs/2026-11-10-languages-design.md`): the six built-in languages for resident pages and emails. How it works, and how to correct or add a translation, is in `docs/languages.md`. Plan 16b (`docs/superpowers/plans/2026-11-11-plan16b-languages-owner.md`) added the owner's side: the Languages page (`/security/languages`: languages on and off, the grid default), page wording per language (`/security/wording`), adding a language on the site or from a JSON file (`/security/languages/<code>`), the email and legal editors per language, and a dashboard line when an added language has new texts to translate.
- [x] Use ASP.NET Core localisation: `IStringLocalizer` and `IViewLocalizer` with `.resx` files per page or area, plus a shared resource for common words. (16a: `IStringLocalizer<ResidentText>` over one shared .resx per language; no `IViewLocalizer`)
- [x] Choose the language from a cookie (the language picker in the header), then the browser's `Accept-Language`, then English.
- [x] Starting languages: **en, de, es, fr, pt, ru** (the ones Wifi had). Use Wifi's `.po` files as a first draft for translators, then remove them. (Drafts were AI-made, not from the .po files; the .po files go with Wifi's removal in Phase 3.)
- [x] Dates, numbers and sizes use the chosen culture (`RegionAdvice.FileSize` and friends included).
- [x] Make the layout work right-to-left (`dir="rtl"`) so Arabic or Hebrew can be added later without a redesign.
- [x] A test that fails when a key is missing from any language file, or when a page has a hard-coded English string (a scan of `.cshtml` files).
- [x] **[X]** Make sure `InvariantGlobalization` is **off** and ICU is present: `libicu72` on Debian 12, `libicu76` on Debian 13. Add it to the `.deb` dependencies and test the culture switch on both. (`install.sh` installs the right ICU per release. 2026-11-11: release.yml run 42944379220 for `fd43ef5` passed the Debian 12 and 13 and Windows smoke runs; on the Debian 12 VM `/guest/sign-in` rendered in all six languages from the `dg-lang` cookie, `Accept-Language: fr-CA` gave French, a junk cookie gave English, `/welcome?lang=ru` gave Russian with `public, max-age=60` and ignored the cookie, the staff `/login` stayed English, and the picker POST set `dg-lang=ru` (303) and refused a POST without a token (400). Signed-in pages, emails and Firestorm are the owner's check.)
- [x] Contributor notes: how to add a language (copy the `.resx`, translate, add to the supported list).
- [ ] The owner's check on the VM for plan 16b: turn a language off and see a resident who chose it get the default; add a language, translate a few texts and upload a file; reword the front page in German; write a German Terms translation, change the English source and see the German readers get English with the note; make German the default.

## Phase 2 — Wifi features, rebuilt

Each item says what Wifi did, how v7 should do it better, and the security rules.

### 2.1 Resident sign-up (Wifi: `newaccountform`, `avatarselectitem`)
> First version done in plan 9 (`docs/superpowers/plans/2026-10-31-plan9-sign-up.md`, design `docs/superpowers/specs/2026-10-31-sign-up-design.md`): `/signup`, an email link (or none when email isn't set up), `/guest/connect`, the owner's switch on the Legal page, and clean-up of sign-ups nobody confirmed.
- [x] Sign-up form: first name, last name, email, password.
- [x] Starter avatar: built in the starter avatars plan (`docs/superpowers/plans/2026-11-11-starter-avatars.md`, design `docs/superpowers/specs/2026-11-11-starter-avatars-design.md`, owner docs `docs/starter-avatars.md`). Step 2 of `/signup` shows the owner's snapshots; `create user` names the picked model, so Robust copies its outfit; the owner loads, dresses, checks and arranges them at `/security/starter-avatars`.
  - [ ] The owner's check on the Debian 12 VM, in Firestorm: load FULLAVATARS, add a snapshot for Lyra, log in as her model and Replace Outfit, press Check it; then sign up a test resident with Lyra and log in. They are wearing her, and her pieces are in their Clothing folder. Sign up once more with a model whose outfit is missing an item and see the warning on the Starter avatars page.
- [x] **[S]** Password rules (minimum length, check against a common-passwords list), the honeypot and rate limit from 1.3. Decided (owner): a taken name is said plainly; a taken email address is never said (its owner gets a notice email).
- [x] Modes chosen by the admin: open, email confirmation required, admin approval required, or closed. (Plan 11: open or closed; with email, the link or staff approval, whichever comes first; without email, staff approval or straight in, the owner's switch.)
- [x] Copying the starter avatar's outfit: Robust copies it inside `create user`; GridGate checks the copy afterwards in the background, slot by slot, and takes an avatar off sign-up when a copy comes out incomplete.
- [x] The viewer's "create account" link (`register=` in `Robust.HG.ini.proto`) points to the new page.

### 2.2 Pending accounts (Wifi: `userpendinglist`)
> Done in plan 11 (`docs/superpowers/plans/2026-11-01-plan11-sign-up-approval.md`, design `docs/superpowers/specs/2026-11-01-sign-up-approval-design.md`): a New sign-ups card on the dashboard, `/people/sign-ups` with search and sort, approve and deny, an hourly email to approvers and a menu badge.
- [x] An admin list of accounts waiting for approval: approve or reject, with an email to the user.
- [x] Pending accounts can't log in to the grid: they sit at level −1 on the launcher's sign-ups board (plan 9, plan 11). No Wifi pending accounts are migrated: v7 never used Wifi's pending marking.

### 2.3 Password recovery (Wifi: `forgotpasswordform`, `recoveringpassword`)
- [x] "Forgot password" sends a single-use reset link (30-minute expiry, token hashed when stored).
- [x] **[S]** Same response whether or not the account exists, rate limited by email and IP, and every session ends after a reset.
- [x] The viewer's "forgot password" link (`password=` in `Robust.HG.ini.proto`) points to the new page.
- [x] Owner-chosen rules per group (residents, staff, owner) set from Setup's "What kind of grid is this?", with two-factor for residents asleep until a rule asks for it (plan 8b, `2026-10-31-password-recovery-design.md`).

### 2.4 Resident account page (Wifi: `useraccountform`)
- [x] Change email (confirmed by a link sent to the **new** address, with a notice to the old one) and change password (asks for the current password). (Plan 10, `/guest/account`.)
- [x] See their own details. (Plan 10.)
- [x] Choose language. (Plan 16a: on the account page, saved in `<data>/guests/languages.json`.)
- Decided: residents **cannot** delete their own account. Only an admin can (2.5).

### 2.5 Admin user management (Wifi: `userlist`, `usersearchform`, `usereditform`, `userdeleteform`)
> Designed with the owner (`docs/superpowers/specs/2026-11-03-people-admin-design.md`) in three parts. On a public grid, protecting people is the owner's duty (owner, 2026-11-03).

#### 2.5a People admin — built in plan 14 (`docs/superpowers/plans/2026-11-03-plan14-people-admin.md`)
- [x] Search with paging (no "list everyone" query against a big table). `/people` is a list searched by part of a name, filtered by role, 25 to a page, with last seen. Each row has task icons: edit, reset link, sign out everywhere, suspend or unsuspend, delete. A greyed icon says why.
- [x] Edit name and email on the person's request, on `/people/{id}/edit`. The old name is held for 30 days, the person is told by email, and an email change tells the old and the new address. Roles stay on the Staff page; there is no free-form level (owner: the grid uses only resident, supervisor, admin and owner).
- [x] **[S]** Every task is checked again on the post and audited. Nobody changes their own account or the owner here, and staff are made residents on the Staff page first.
- [x] Delete is delete (owner): only for someone already suspended, after "Are you sure?". It removes everything in Robust tied to the account, sends their regions back to their home estates, and removes their empty estate. Assets stay.
- [x] No admin-set passwords. Staff send a reset link, which also clears two-factor once used (owner), so a hijacked account can be given back.
- [ ] The owner's check on the VM, in Firestorm:
  - search and page
  - rename and log in again
  - an email change with both notices
  - a reset link with two-factor on
  - sign out someone in-world
  - suspend, then delete

#### 2.5b Inventory takedown — built in plan 15 (`docs/superpowers/plans/2026-11-04-plan15-inventory-takedown.md`)
- [x] The owner and admins open a resident's inventory (`/people/{id}/inventory`), by folder or by searching item names (Trash too), and take down the items a notice names. Item names, types and creators only; content is never shown.
- [x] Each takedown is a case (`TD-2026-001`) with the notice, the people, the items, the evidence copy (the stored asset file and its SHA-256) and every event. A printable page for the sender leaves out the holders. Put back restores an item exactly as it was; Find copies takes down copies others hold; Close ends the case.
- [x] **[S]** The resident is told by email for copyright and rules cases, never for illegal content. Opening an inventory and every step is audited, with names and the case number only.
- [x] A **hold**: anyone an open case names can't be deleted, and unreadable case files hold everyone. `AccountHolds` is the one place delete asks; 2.5c adds its own reasons there.
- [ ] Not covered: objects already rezzed in regions (staff return them in-world), and copies taken to other grids. Exporting evidence for a legal request is 2.5c.
- [ ] The owner's check on the VM, in Firestorm:
  - take an item down from a signed-out test resident, log in as them and see it gone
  - Find copies on a second resident and take that down too
  - try to delete the first resident and be refused because of the hold
  - put one item back and see it in the same folder
  - close the case, print the sender's page, and then delete works

#### 2.5c History for legal requests — moved to "Later" (owner, 2026-11-05)

### 2.6 Terms of service (Wifi: `termsofservice`, `tos`)
- [ ] The admin edits a ToS text per language, with a version number. (Plan 16b: the owner writes the Terms, Privacy and Cookies pages per language at `/security/legal`. The default language is the source and owns the version number; a translation carries the source's version and is shown only while it matches. Versions per language on their own, and the acceptance table below, are still this item's.)
- [ ] Accepted at sign-up. When the version changes, the next website sign-in asks again.
- Decided: **grid login is blocked** until the current version is accepted.
  - Wifi never did this. It only recorded acceptance on the web, in a `TOS` column it added to `GridUser`. Core OpenSim has no ToS check in login.
  - [x] Add a small DreamGrid patch to `LLLoginService`: when the avatar hasn't accepted the current ToS version, refuse the login with a localised message that names the URL where they can accept it. The patch asks the launcher (loopback only) or reads a launcher-owned table. (Done: login asks `/dreamgrid/terms` on loopback and answers reason `tos` with the public `/terms` page, which Firestorm shows in its Terms window; "I agree" logs in again with `agree_to_tos`, recorded like a website acceptance. If the launcher can't be reached, the login goes ahead. Hypergrid visitors aren't asked. English only until 1.5.)
  - [x] Admins (level 200 and above) are never blocked, so the grid can't lock out its owner. (The owner and supervisors aren't asked either.)
  - [x] Saving a small fix to the Terms shouldn't make everyone accept again: a "small fix, don't ask again" choice when saving. (A "Small fix only" tick on the Legal page keeps a written page's version; audited with "small fix".)
  - [x] Try it with a real Firestorm. (2026-11-01: Firestorm 7.2.5 on Windows against a Debian 12 grid: the Terms window showed the page, "I agree" logged in, and the acceptance was recorded as "in the viewer"; the owner and admins weren't asked.)
  - [ ] A grid on Windows, and what other viewers (Singularity, Cool VL Viewer) show for a `tos` reply.
  - [x] Store acceptance in a launcher-owned table: avatar, ToS version, time, IP. (Done: `Guests/TermsAcceptances.cs`, `<data>/guests/terms.json`.)
  - [ ] Move existing values from `GridUser.TOS` into it during the upgrade (Phase 3).

### 2.7 Viewer splash page (Wifi: `splash`, `welcome`)
> Redesigned with the owner (design `docs/superpowers/specs/2026-11-01-viewer-splash-design.md`): the owner (or an admin) builds the page from blocks placed anywhere, with an optional snap grid; it shrinks to a readable size and then scrolls. Three plans: 12a the stored design and the public page (done, `docs/superpowers/plans/2026-11-01-plan12a-splash-page.md`), 12b pictures (done), 12c the block editor (done, `docs/superpowers/plans/2026-11-02-plan12c-splash-editor.md`).
- [x] A welcome page for the viewer's login screen: grid name, online count, region count, news text the admin edits. (12a: `/welcome` draws the published design or a built-in default with live counts for the whole grid; designs are built and published in the editor at `/splash`. English only until 1.5.)
- [x] **[S]** Read-only and cacheable; no admin data in it. (12a: counts only, no script, `Cache-Control: public, max-age=60`, layout in a generated stylesheet so `style-src 'self'` holds.)
- [x] Try it in a real Firestorm. (2026-11-01: Firestorm on Windows against the Debian 12 VM showed the default splash page behind the login boxes and the owner said it looks right; the security headers `frame-ancestors 'none'` and `X-Frame-Options: DENY` don't get in the way.)
- [x] Pictures: background, picture blocks, the grid logo, place pictures (12b, `docs/superpowers/plans/2026-11-01-plan12b-splash-pictures.md`). Uploads are checked by their contents (PNG, JPEG, WebP; 10 MB; 50 megapixels), redrawn with SkiaSharp (colour profiles converted, EXIF dropped, at most 3840 px) and kept under random names; residents see only the published page's pictures; unused ones go after 30 days; a region's map tile can be copied in. Packages now publish the launcher per system, so SkiaSharp adds about 5 MB rather than 466 MB; `libSkiaSharp.so` needs nothing extra on Debian 12. Seeing pictures in Firestorm is part of the owner's check below.
- [x] The block editor at `/splash` (12c): a "Splash page" menu item for the owner and admins on the guest side. Blocks come from a shelf (drag or click); move, resize from any edge or corner, arrow keys, snap to a grid, guide lines to other blocks and the page centre; turn a block with its round handle (Shift for 15° steps) or an angle field; bring forward, send back, duplicate, delete; undo and redo; page and block settings (background picture, focus point, darkening, colours, see-through, text style, each kind's content); a picture panel (upload, used-by, delete, map tiles, the grid's logo). The draft saves itself a second after each change and is drawn by the same code as `/welcome`; a stale save from a second tab is refused with a message. Previews at 1024 × 640, 1280 × 800 and 1920 × 1080 show where the page stops shrinking; `/splash/preview` shows the draft full size.
- [x] The owner's checks: build a page on the VM, publish it, and look at it in Firestorm (pictures, logo, shrink then scroll in a small window, numbers within about a minute, what the links do). (2026-11-02: Firestorm against the Debian 12 VM running `36e7a93`; the owner said it works.)

### 2.8 Groups (Wifi: `groupslist`, `groupeditform`, `groupmembers-*`)
> Designed with the owner (`docs/superpowers/specs/2026-11-02-groups-admin-design.md`), built in plan 13 (`docs/superpowers/plans/2026-11-02-plan13-groups-admin.md`).
- [x] An admin view of groups: list, rename, change charter, list members, add or remove a member, delete a group. (Plan 13: "Groups" in the menu for the owner and admins, on the guest side. A member is added by avatar name straight into the Everyone role, with no invitation and their active group unchanged; hypergrid visitors are shown and can be removed but not added; removing a group's last owner needs a second press, deleting a group a ticked box. Changes reach the viewer within about a minute.)
- [x] A default group for messaging: an admin marks one group the default; residents join it when their website sign-up becomes active (open, confirmed by email, or approved by staff), and **Add everyone now** puts in everyone else. Leaving is the resident's choice: the launcher remembers who has been in it (`guests/group-joins.json`, written before the group) and never puts them back.
- [x] Robust already runs core OpenSim groups (`OpenSim.Addons.Groups.dll`). `1DivaOpenSimServices` is loaded only by Wifi. Its `GroupsService` adds just three admin actions that core OpenSim doesn't offer: rename/charter, delete a group, remove a member. It also has no permission checks of its own.
- [x] Decided: the launcher does those three actions itself, in one database transaction each, on the core `os_groups_*` tables. Only signed-in admins can use them, and each one goes in the audit log. `1DivaOpenSimServices` is deleted in Phase 3.
- [x] The owner's check on the VM, in Firestorm: rename a group, add and remove someone, make a default group and press Add everyone now, sign up a new resident, and see the group change in the viewer. (2026-11-02: Firestorm against the Debian 12 VM running `b16af25`, PR 28 on V7; the owner said it works.)

### 2.9 Hypergrid links (Wifi: `linkregion*`, `HyperlinkServiceUserLevel`)
> Built without questions (owner, 2026-11-05: "use your best judgement"); design `docs/superpowers/specs/2026-11-05-hypergrid-links-estates-design.md`, where the choices that were mine are marked.
- [x] The owner, admins and supervisors add or remove hypergrid links (owner, 2026-11-03); no extra "privileged" level is needed. `/hypergrid`, a "Hypergrid" menu item for all staff, only where this computer runs the grid. A link goes on a free cell just above the grid unless X and Y are typed; Robust makes it (`link-region`). Removing deletes only a Hyperlink row, by UUID.
- [x] **[S]** Check the address format and allow only http or https; the link test runs with a timeout and never follows redirects to private networks. The launcher fetches `get_grid_info` itself first: 10 s, 64 KB, no redirects, and only public addresses, checked at connect time. Robust's own requests after that can't be limited from the launcher (written in the design).
- [ ] The owner's check on the VM, in Firestorm: link a region on another public grid, see it on the map, teleport there and back, then remove it.

### 2.10 Estates (Wifi: `estatelist`, `estateeditform`)
> Built in the same change as 2.9; same design document.
- [x] List estates, change the owner, add managers, assign regions to an estate. `/estates` and `/estates/estate` for the owner and admins: rename, owner, managers (at most 10), move one of this computer's regions in (it must be running), make a new estate, delete an empty one. The grid's own estate keeps its name and owner; a region owner's estate keeps its owner, and their regions stay in it.
- [x] Changes are written to the estate tables, then every running region program reads the estate again through a new OpenSim console command, `estate reload <id>` (`OpenSim/Region/Application/OpenSim.cs`).
- [ ] The owner's check on the VM, in Firestorm: add a manager and see them in the Region/Estate window without a restart; change an estate's owner; move a region into a new estate; delete the empty one.

### 2.11 Inventory backups (Wifi: `iars.html`, `inventorylist`)
> Built in the IAR plan (`docs/superpowers/plans/2026-11-11-iar-backups.md`, design `docs/superpowers/specs/2026-11-11-iar-backups-design.md`); owner docs in `docs/inventory-backups.md`.
- [x] Resident: download their own inventory as an IAR (only full-rights items, object contents included, with a list of what was left out) and upload one into a new `Imported <date>` folder (taken-down content removed). Owner and admins: save or load an IAR for any avatar, with no limits and a full record (`/people/{id}/iar`).
- [x] Four switches (download and upload, grid-wide and per resident) and every limit as a setting (`/security/iar`, and per resident on their People page).
- [x] Reuse the OAR library's streamed upload and download pattern (size limits, staging folder, safe names).
- [x] **[S]** Download links are short-lived and tied to the session; IARs are never served from a guessable path.
- [ ] The owner's check on the Debian 12 VM, in Firestorm:
  1. Turn downloads on, back up a resident, and read the left-out list. It must include a full-perm box holding a no-transfer item.
  2. Upload that IAR into a second test resident, and see `Imported …` in their inventory.
  3. Upload an IAR holding a taken-down item: it's left out, and the case shows the match.
  4. Hit the daily limit once, and try someone else's download link.
  5. As an admin, save a switched-off resident with a reason and a case number. See the record and the delete hold. Then load FULLAVATARS into a test account from a browser upload.
  6. On the second grid, check that textures on a backed-up object are still there.
  7. Back up a box received from another avatar that was never rezzed, and check what the left-out list says.

### 2.12 Notices to residents — not a Wifi feature
- Wifi has **no** mass email. Its `notification` page is only an on-screen "done / OK" message. Wifi sends just three emails, each to one person: sign-up confirmation, password reset, and "account activated".
- Decided: **not in this work.** Mass email to residents is kept as an idea for later, if wanted (see "Later" at the end).

### 2.13 Not carried over (on purpose)
- [ ] **Web remote console** (`console.html`, `RemoteConsole.html`). Dropped: the launcher already has logs and grid actions. **[?]** Later, maybe an allow-listed command box for admins.
- [ ] **Script engine and custom HTML skins** (`20WifiScriptEngine`, `WifiPages-Custom`). Replaced by a safe theme setting (logo, colours, splash text) with no code from users.
- [ ] **Install form** (`installform`). Already replaced by `/setup`.
- [ ] **Bulk user creation** (`gridusers-creation-progress`). Dropped unless asked for.
- [ ] **Destinations list** (`destinationlist`). Folded into the splash page as "popular regions" if wanted.

### 2.14 Landing regions (Wifi: `1DivaOpenSimServices` fallback regions)
> Built without questions (owner, 2026-11-11: "just start"). Before that, the "Set by Dreamgrid" slot in `Robust.HG.ini.proto` (`; START` … `; END`) was empty, so a login whose home region was stopped could fail.
- [x] A **Landing** section on the Regions page (`/regions`, only where this computer runs the grid): one default region, where people land when their home or last region can't take them, and fallback regions tried when the default is down too. With nothing chosen the default is **Automatic**: the first region, so a new grid has one from the start. A deleted region is dropped from both.
- [x] Decided: the fallbacks are a set, not an ordered list. OpenSim's `GridService.GetFallbackRegions` sorts them by distance from where the login was headed, so a written order would not be kept without an OpenSim patch; the page says the nearest running one is used.
- [x] Hypergrid visitors who name no region land on the default region too (it gets `DefaultHGRegion`).
- [x] The launcher writes OpenSim's own lines into that slot, one per region by UUID (`Landing.Lines`): `Region_<uuid> = "DefaultRegion, DefaultHGRegion, -FallbackRegion"` and so on. Every other region gets the flags taken off with `-`, because Robust adds a line's flags to the region's database row when it registers and never clears them on its own. No change to OpenSim code. A change applies when the world restarts; the page says so. A Region Server writes no lines (its grid decides).
- [ ] Check on the VM: stop someone's home region, log in, and see them land on the default, then on a fallback with the default stopped too.

## Phase 3 — Remove Wifi completely

Do this only after Phase 2 has shipped the features people use, and after 2.2's migration has run.

- [ ] Remove the projects from `DreamGrid.sln` and delete the folders: `addon-modules/21Wifi`, `20WifiScriptEngine`, `ProcessorTest`, `00DivaInterfaces`, `01DivaUtils`, `02AddinExample`, `00Data` (`Diva.Data`, `.MySQL`, `.SQLite`).
  - [ ] Also delete `1DivaOpenSimServices`, once the launcher's group actions (2.8) work and landing regions (2.14) replace its fallback regions.
  - [ ] Before deleting, prove nothing else references them.
- [ ] Remove jOpenSim too. It is end of life: the last release was 0.3.2.0 for Joomla 3, October 2020, and no public source repository could be found.
  - [ ] Remove `jOpenSimSearch` from `DreamGrid.sln`, and delete `addon-modules/jOpenSimSearch` and `addon-modules/jOpenSimProfile` (the profile module isn't in the solution).
  - [ ] Delete `jOpensimURL` from `bin/config-include/Proto/*/OpenSim.ini` and from `Robust.HG.ini.proto` (with its "Apache Port for Jopensim" comments). Also delete the commented `data_services=…/JOpensim/…registersearch.php` line, which has a hard-coded LAN address.
  - [ ] Profiles keep working through OpenSim's built-in profiles service, already set up in the templates (`[UserProfilesService]` in Robust, `[UserProfiles] ProfileServiceURL` for regions). Check that the generated INIs have it switched on and that a profile edit is saved, on Windows and on Debian.
- [ ] Delete from `bin/`: `Wifi.ini`, `config-addon-opensim/Wifi.*`, `config-addon-robust/Wifi.ini`, `WifiPages-*`, `Diva.*.dll/.pdb`, `Diva.Wifi.pot`, `Diva.Wifi.ProcessorTest.*`.
- [ ] Remove the Wifi lines from `.gitignore`, from `bin/config-include/Proto/*/OpenSim.ini`, and from `bin/OpenSim.ConsoleClient.ini`.
- [ ] `Robust.HG.ini.proto`: the `register=` and `password=` lines point to the new pages (2.1, 2.3).
- [ ] `GridConfigWriter.SetSmartStartAndWifi`: stop writing `[WifiService]`; rename the method; update `GridConfigWriterTests`.
- [ ] `tests/DreamGrid.Smoke/SmokeRun.cs`: remove the `[WifiService]` stub and check that nothing merges a leftover `Wifi.ini`.
- [ ] Upgrades: the Windows installer and the `.deb` delete old Wifi files and DLLs from an existing install, so OpenSim doesn't load a stale add-in.
- [ ] Database (decided: clean it up on upgrade). Wifi has no tables of its own. Its only schema change is a `TOS` column added to the core `GridUser` table, through its own `GridUserStore` migration version 2 (core OpenSim's version 2 is empty).
  - [ ] Move the `TOS` values into the new acceptance table (2.6), then drop the `GridUser.TOS` column.
  - [ ] Leave the `migrations` row for `GridUserStore` alone, so core OpenSim doesn't try to run its migrations again.
  - [ ] Make sure Robust's `[DatabaseService] StorageProvider` is the core `OpenSim.Data.MySQL.dll`, not `Diva.Data.MySQL.dll`. Wifi's `Wifi.ini` switches it, as the smoke test notes.
  - [ ] Before any of this, back up the Robust database.
- [ ] Update the spec (section 4.8 and the feature inventory) and the user manual pages that mention Wifi.
- [ ] A final `grep -ri wifi` in CI that finds nothing outside the release notes.

## Phase 4 — Works on Windows, Debian 12 and Debian 13

- [ ] CI matrix: Windows Server (latest), Debian 12 and Debian 13 containers. Run unit, integration and the smoke run on all three.
- [ ] End-to-end checks per system: sign-up, confirmation email (against a local test SMTP server such as smtp4dev or MailHog), password reset, language switch, HTTPS start-up, IAR upload and download.
- [ ] **[X]** File paths: only `Path.Combine` and the `LauncherPaths` helpers; compare file names case-sensitively on Linux.
- [ ] **[X]** Packages: ICU (1.5) and CA certificates (for SMTP TLS and ACME) in the `.deb` dependencies; nothing extra needed on Windows.
- [ ] **[X]** Services: the systemd unit (capabilities for ports below 1024, a writable data folder) and the Windows service account (rights to the certificate and keys folder).
- [ ] **[X]** Text files the site writes (email templates, ToS) use UTF-8 without a BOM and `\n`; reading accepts both line endings.
- [ ] Manual test pass on a clean Debian 12 VM, a clean Debian 13 VM and a clean Windows 11 PC before each release.

## Code clean-up — duplicate code (review 2026-11-12)

> Start after PR 70 (IAR backups) and PR 71 (starter avatars) are merged (owner, 2026-11-12). A read-only review of `Launcher/` and `OpenSim/Tools/GridGate.ModelImport/` found these copies, about 500 lines in all. None may change behaviour: each item lists what the copies do differently, and that must survive the merge. Line numbers are from `main` at 8907cff2 and will drift.

- [ ] **1. Database connection and command code (~80–100 lines).** The connection-open lines (`settings()` → `new MySqlConnection(MariaDbServer.ConnectionString(…))` → `OpenAsync`) appear at 18 sites, for example `Core/Database/WorldCounter.cs:101`, `AccountRemover.cs:87,116`, `GridRegions.cs:43`, `OnlineUsers.cs:45`, `RobustEmails.cs:34`, `RobustNames.cs:42`, `RobustPasswords.cs:50`. Five `RunAsync`-style wrappers repeat open → work → catch-and-rethrow (`GridEstates.cs:193`, `GridGroups.cs:218`, `GridHyperlinks.cs:107`, `PeopleDirectory.cs:104`, `InventoryStore.cs:265`). `Command`/`ExecAsync` are word for word the same in `GridEstates.cs:209/216` and `GridGroups.cs:238/245`, with near copies in `PeopleDirectory.cs:120`, `InventoryStore.cs:240` and `RegionStore.cs:151`. The Guid-column parse repeats `InventoryStore.IdOf` (`GridEstates.cs:223`, `GridRegions.cs:51`, `OnlineUsers.cs:53`).
  - Fix: `OpenRobustAsync` / `OpenOpensimAsync` next to `MariaDbServer.ConnectionString`, which dispose the connection if the open fails, as `RegionStore.OpenAsync` already does. Add one shared `Command/Exec/Scalar` and one generic `RunAsync`, and reuse `IdOf`.
  - Keep: each wrapper's own exception type and message; GridGroups catches `DuplicateKeyEntry` first; InventoryStore's narrower filter (no `OverflowException`, lets `NoInventoryException` through) and its `ct` on `OpenAsync`; GridEstates' name lookup swallows errors where GridGroups' throws.
- [ ] **2. Error/notice boxes in the page views (~100 lines).** The `@if (Model.Error is not null) { <div class="error-box error alert alert-danger" role="alert">…` block appears 43 times under `Pages/`, usually followed by the `ok-box alert alert-success` notice. The Regions pages use a second notice style, `<p class="notice alert alert-info py-2">`.
  - Fix: a tag helper beside `ButtonIconTagHelper` (`Navigation/ButtonIcons.cs`) that writes nothing when the text is null, or a `Shared/_StatusBoxes.cshtml` partial. A partial must take a record or tuple: with a bare null string as its model, Razor passes in the parent page model instead, and the page throws.
  - Keep: the exact class strings (`alert-info` on the two TwoFactor pages and SignUps, no `ok-box` on TwoFactor and PasswordReset); Translate shows the notice before the error; leave out the legal-link variant in Languages.
- [ ] **3. Audit-log writes (~60 lines).** About 25 copies of `AuditActor.Of(…)` → `audit.Write(new AuditEntry(…, ip, action, target, result))`. There are private `Write` helpers in 10 pages (Security Bots, Email, EmailWording, Index, Languages, Legal, Translate, Wording; Advanced/PasswordReset; Regions/Region) and in 7 classes (`PeopleActions`, `TakedownActions`, `StaffActions`, `StaffTickets.Audit`, `EstatesAdmin`, `GroupsAdmin`, `HypergridAdmin`). `Endpoints/SplashApi.cs` has 7 more inline.
  - Fix: one extension method next to `AuditActor` in `Security/AuditMiddleware.cs`: `Write(this AuditLog, ClaimsPrincipal actor, string? ip, string action, string? target, string result = "ok")`.
  - Keep: EstatesAdmin trims the target with `LogText.Clean(target, 300)`; GroupsAdmin's target can be null; some callers always write "ok".
- [ ] **4. Two-factor setup: staff and guest copies (~100 lines).** `Pages/Guest/TwoFactor.cshtml.cs:142-210` and `Pages/Profile/TwoFactor.cshtml.cs:169-252` have the same helpers (`Locked`, `Wrong`, `StartEnrol`, `ShowSecret`, `Unprotect`, `Protector`, `AccountName`, `Failed`, `Entry`) and the same TurnOn/TurnOff/NewCodes bodies. In `Pages/Security/Index.cshtml.cs`, `OnPostReset` (89-121) and `OnPostResetResident` (128-160) are the same handler.
  - Fix: a `TwoFactorSetupPageModel` base class that each page fills in (store, `SignInDoor`, purpose, audit role, texts, who is signed in). In Security/Index, one private `Reset(store, id, damagedText, signOut)`.
  - Keep: the two `ShownPurpose` strings (changing one breaks secrets already shown); guest answers `NotFound()`, staff `Challenge(AuthSchemes.Admin)`; staff TurnOn re-issues the cookie and calls `TwoFactorGate.Forget`; staff TurnOff refuses when two-factor is required; localized guest texts vs English staff texts; log prefixes. For the reset: `twoFactor` vs `residents.Store`, and `sessions.EndAll` vs `guestStamps.Rotate`.
- [ ] **5. GridController operation lock (~65 lines, medium confidence).** `await _ops.WaitAsync(ct); _activity = …; try {…} finally { …; _ops.Release(); }` appears 29 times in `Core/Grid/GridController.cs` and `GridController.OtherGrid.cs:265`. `LiveRegion` (380) is also inlined again at 198, 393, 516 and 1056.
  - Fix: `ExclusiveAsync(GridActivity? activity, Func<Task<T>> work, ct)` plus a void overload.
  - Keep: `_activity` is set after the wait and before the `try`; `RegionPowerAsync` works its activity out from the action; `MoveRegionAsync` switches to Restarting partway through and still resets to None at the end; the operations with no activity never touch `_activity`; line 1276 throws `ArgumentException`, unlike `LiveRegion`.
- [ ] **6. Two-factor sign-in: staff and guest copies (~65 lines).** `Security/TwoFactorPending.cs` and `Guests/GuestTwoFactorPending.cs` differ in only 20 lines (Issue, Read, Delete, Protector, Options are the same). `Pages/Guest/SignInCode.cshtml.cs:31-88` and `Pages/Login/Code.cshtml.cs:35-92` are the same code step.
  - Fix: one generic pending-cookie class with two thin wrappers, and a shared `TwoFactorCodeStep.VerifyAsync` that both pages map onto `Page()` or a redirect.
  - Keep: each cookie's name, path and purpose string (a new purpose would invalidate cookies already issued); staff `Read` also refuses `StaffRole.None`; the sign-in calls, audit roles, landing pages and back-to-password pages differ.
- [ ] **7. Helpers that already exist, written out again (~60 lines).**
  - `WebsiteAddress.Base(s)` is rebuilt as `$"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}"` in `Guests/AccountChanges.cs:289`, `SignUpApprovals.cs:167`, `SignUps.cs:324`, `SignUpNotices.cs:52`, `PasswordResets.cs:122,147`, `Security/PeopleActions.cs:160` and `CommandLine.cs:124`.
  - `Estates/AvatarLookup.FindAsync` is copied line for line in `Pages/Groups/Group.cshtml.cs:43-63` and `Pages/Staff/Index.cshtml.cs:44-62`. Only the log prefix differs, so give it a prefix parameter. `Pages/Regions/Region.cshtml.cs:214-239` is close but has its own texts.
  - `OwnerEmailAsync` is identical in `Pages/Security/Email.cshtml.cs:226` and `EmailWording.cshtml.cs:181`.
  - `Full(RobustAccount)` is copied in `EstatesAdmin.cs:207`, `GroupsAdmin.cs:217`, `AccountChanges.cs:287`, `SignUpApprovals.cs:169` and `SignUps.cs:326`, with about 11 more inline copies. Make it an extension method, not a property: `TwoFactorPendingSignIn` is serialised into the 2FA cookie.
  - `Pages/People/Delete` and `Edit` write their own `MayDelete`/`MayEdit`, which are exactly `TakedownAccess.MayUse`.
- [ ] **8. State files written by hand outside `JsonStateFile` (~30 lines).** The shared-read stream (`ReadWrite|Delete`, then ReadToEnd) is in `PendingEmailChanges.cs:73`, `SignUpBoard.cs:153`, `OwnerLanguages.cs:164`, `LoginThrottle.cs:166` and `SettingsStore.cs:38`. The `.damaged-<utc>` set-aside is in `OwnerLanguages.cs:204`, `PendingEmailChanges.cs:94` and `SignUpBoard.cs:182`. "Create the folder, then `AtomicFile.WriteAllText`" is in `ApprovalNotices.cs:69`, `TicketNotices.cs:92`, `PendingEmailChanges.cs:101` and `SignUpBoard.cs:189`.
  - Fix: `AtomicFile.ReadShared`, `AtomicFile.SetAside` and a write overload that creates the folder. Don't change `WriteAllText` itself: TakedownStore relies on it requiring the folder to exist.
  - Keep: these stores re-read on every call and throw on a failed write, which is why they aren't `JsonStateFile`. A failed set-aside throws in PendingEmailChanges and SignUpBoard but is logged in the others; TicketNotices swallows IO errors, ApprovalNotices doesn't.
- [ ] **9. Region background actions and Regions page redirects (~20–45 lines).** The "requested → `background.Run` → finished, catch cancel/failure" block appears 4 times in `Endpoints/RegionActions.cs` (`TryMove` 39, `TryChangeRunsWith` 67, `RunDeleted` 115, `TryRun` 137) and once in `GridActions.cs:52`. `Pages/Regions/Region.cshtml.cs` repeats `{ Notice = …; return Redirect("/regions/region/" + id); }` 6 times, and "… is still running. Try again when it finishes." appears 11 times.
  - Fix: turn `RunDeleted` into `Run(what, name, work)` with the `PortConflictException` catch from `TryRun`; a page `Back(id, notice)` and one `BusyNotice`; move `Import`'s `Start(OarJobRequest)` to `OarJobs` and reuse it in Region and Settings.
  - Keep: the log wording (folding in TryMove and TryChangeRunsWith would change it, so ~20 lines if they stay out); Settings redirects to `…/settings/basics`.
- [ ] **10. Mesh importer: bounding boxes and sub-faces (~30 lines).** The same box loop is in `Building/UnitMesh.cs:49` (`Box`), `Checking/ModelChecker.cs:148` (`LocalBox`) and `Building/Simplifier.cs:38` (`Diagonal`). `MeshAssetWriter.cs:193` rebuilds the min/max that `Domain` (236) already has. `Simplifier.Compact` (75-99) and `FaceSplitter.Piece` (79-100) copy the same vertex subset.
  - Fix: one `Bounds(IEnumerable<ImportFace>)` returning `(lo, hi, any)`; make `Piece` a shared helper and have `Compact` call it.
  - Keep: an empty mesh gives zero vectors in `Box` and 0 in `Diagonal`, while `MeshAssetWriter`'s `Aggregate` throws; leave `ModelChecker.Overall` alone (it transforms each vertex first).
- Smaller copies, if there's time: the look-up/refuse/audit opening in `PeopleActions` (`SendResetAsync`, `SignOutAsync`, `UploadRightAsync`, `DeleteAsync`) and `TakedownActions` (`MayAct` + case checks); the retry loop in `Core/OpenSim/RestConsoleClient.cs:98` and `:127`; the Robust map-tile fetch in `Endpoints/RegionsApi.cs:31` and `SplashApi.cs:169`; TakedownStore's Save/Update write-and-log (`TryWrite` like TicketStore); the outcome → Notice/Error split in the Estate, Group and Hypergrid `ShowAsync` methods.
- [ ] **[?]** The "Robust didn't answer" exception filter has about 17 copies that don't agree (`Estates/AvatarLookup.cs:23`, `Guests/PasswordResets.cs:89,171`, `Security/PeopleActions.cs:99`, `SignUpNotices.cs:45`, `TicketSweep.cs:108`, `SignUpCleanup.cs:111`, `SignUps.cs:320`, `SignUpApprovals.cs:163`, `AccountChanges.cs:272`, and pages). `SignUpApprovals` has no cancellation check, `AccountChanges` checks it only for `TaskCanceledException`, and some include `StaffDirectoryUnavailableException`. Decide which is right before merging them into one `RobustFailure.IsUnreachable`; it may be a bug, not just a copy.

---

## Suggested order

1. Phase 0 (now).
2. 1.1 HTTPS, 1.3 security baseline, 1.5 languages — so every new page is born secure and translated.
3. 1.2 audiences and roles, 1.4 email.
4. 2.3 password recovery → 2.1 sign-up → 2.2 pending → 2.4 account page → 2.6 ToS → 2.7 splash.
5. 2.5 admin users → 2.8 groups → 2.9 hypergrid → 2.10 estates → 2.14 landing regions → 2.11 IAR → 2.12 notices.
6. Phase 3 removal, then a Phase 4 release check.

Phase 4's CI matrix should be set up early (alongside step 2) so every phase is tested on all three systems as it lands.

## Decisions

| # | Question | Answer |
|---|---|---|
| 1 | Rewrite git history to purge the leaked secrets? | **No.** Revoke them instead. |
| 2 | Resident area on the same site and port as admin? | **Same port.** |
| 3 | Bot protection | **All options, each with an on/off switch.** A and B on by default, C off; admin approval available as a switch (1.3). |
| 4 | Block grid login until a changed ToS is accepted? | **Yes** (2.6; needs an OpenSim login patch). |
| 5 | Residents delete their own account? | **No.** |
| 6 | Keep `1DivaOpenSimServices` (groups)? | **No.** The launcher does the three group admin actions itself, admin-only and logged (2.8); the module is deleted (Phase 3). |
| 7 | Mass email notices? | Wifi never had them. **Not now**; kept for later (see "Later"). |
| 8 | Remove Wifi's database changes on upgrade? | **Yes** (Phase 3: the `GridUser.TOS` column). |

### Bot protection options (decision 3)

All options keep the honeypot field, the rate limit per IP, and email confirmation or admin approval. Those already stop most simple bots.

| Option | How it works | Good | Bad |
|---|---|---|---|
| A. Honeypot + rate limit only | A hidden field that only bots fill in, plus a cap on sign-ups per IP per hour. | Nothing for people to do; nothing to translate; no outside service. | A bot written for this site gets past it. |
| B. A + proof-of-work (for example ALTCHA, open source, MIT) | The browser solves a small puzzle (about a second) before the form posts. The launcher checks it. | Invisible to people, accessible, no tracking, works offline on a LAN, the same on Windows and Debian. | Slows bots but doesn't stop a determined one; needs JavaScript. |
| C. A + self-hosted image CAPTCHA | Distorted text or images that people type in. | Familiar. | Hard for people with poor sight; needs translating; modern bots solve them; most work to build. |

Decided: build **all three**, each with its own switch. A (honeypot and rate limit) and B (proof-of-work) are on by default, and C (image CAPTCHA) is off by default. Admin approval of new accounts is a separate switch. The details are in 1.3.

## Later (not in this work)

- Advanced install: let the owner choose the database password (owner, 2026-10-30).
- HTTPS for the website (owner: later, 2026-10-30).
- Office 365 / Microsoft 365 email, which needs OAuth sign-in (owner: later, 2026-10-31).
- Region owners for regions on a Region Server (the guest side exists only where the grid service runs).
- OAR download for region owners.
- Audio alternative for the image CAPTCHA.
- 2.5c History for legal requests (owner: later, 2026-11-05; designed in `docs/superpowers/specs/2026-11-03-people-admin-design.md`). Collect what the grid holds about an avatar and save it as a file to hand over: logins and their addresses, account changes, staff actions, regions and groups. First check what OpenSim really records; chat isn't stored. The same hold as 2.5b (its own reason in `AccountHolds`), so a suspend-then-delete can't destroy what a request needs.
- Mass email to residents (to everyone or to one group), queued and throttled, with an unsubscribe link. Add it only if it's asked for.
- Faster asset loading through Cloudflare (owner, 2026-11-04: "we will be testing this out and moving in that direction"). Viewers download textures and meshes from Cloudflare's cache instead of the grid machine's upload, on the free plan, through a Cloudflare Tunnel to Robust. Notes, OpenSim changes and stages: `docs/superpowers/specs/2026-11-04-cloudflare-assets-design.md`.
  - [x] Robust's GetTexture connector didn't load on Linux (misspelled DLL and class name), so `/CAPS/GetTexture/` answered 404. Fixed in PR 35.
  - [ ] Stage 1, by hand on the VM: the owner sets up the tunnel, WAF rule and cache rule; Welcome's caps point at `assets.<domain>`; Firestorm check. Answers which caps Firestorm uses and whether hypergrid visitors still load.
  - [ ] OpenSim: Robust mesh connector on; a Robust `ViewerAsset` connector that serves only drawable types (never scripts or notecards), if Firestorm needs it; `Cache-Control` on asset answers.
  - [ ] Launcher: an "Asset address (CDN)" setting that writes the region caps, and a test button.
  - [ ] **[S]** Before any public grid turns it on: taken-down assets are blocked in Robust and purged from Cloudflare.
- Smart Start: sleeping regions (owner, 2026-11-08: on hold until the physics work is merged and running well). An idle region's program is frozen and its memory pushed out, then woken when someone arrives: the frozen region wakes in about 0.01 s, where starting DragonFyre from cold takes 19 s. Design, test results and stages: `docs/superpowers/specs/2026-11-05-smart-start-design.md`.
  - [x] Linux freeze tested on the VM (cgroup freezer, `memory.reclaim`, wake on login and on teleport), 2026-11-05.
  - [x] Windows suspend tested on a bench on the owner's PC (`NtSuspendProcess`, `EmptyWorkingSet`), 2026-11-08 (spec A.5). It works. TCP knocks can be seen while a region is suspended; UDP knocks can't.
  - [ ] OpenSim fix first: after a sleep longer than 10 minutes, OpenSim's thread-pool watchdog (`Util.ThreadPoolWatchdog`) cancels every job that was mid-run when the region froze (9 jobs in the 30-minute Windows test). Same code on Linux. The fix: the watchdog notices the gap in its own runs and restarts the jobs' clocks instead of cancelling them (spec section 7, item 5).
  - [ ] Stage 1, sleep and wake by hand: a cgroup per program with `Delegate=yes` (Linux), a suspend and trim (Windows), "Sleep now" / "Wake now" on the power menu. First prove that the `dreamgrid` user can freeze without root.
  - [ ] Stages 2–4 as in the spec: automatic sleep after the idle delay (save first), the Smart Start hook's `?alt=` wake, the knock watcher, always-on regions and neighbours, the swap file, stopped regions on demand.
  - [ ] Windows under memory pressure: check that trimmed pages really reach the page file and how fast they come back (the test PC has 63 GB, so it wasn't tested).
- In-viewer search (places, people, classifieds, events). Owner (2026-11-01): wanted later as a full "post an ad and search" built into the grid: look for groups, ads (classifieds), avatar info, land, land for sale and events, and post ads and events. Today `SearchURL=` is empty in the region template, so Search finds nothing. Once there is an events list, the splash page (2.7) can show upcoming events. Keep the `OpenSimSearch` region module (upstream: `kcozens/OpenSimSearch`); it needs a search web service at `SearchURL`. Two ways to provide one:
  - point it at [magicoli/opensim-helpers](https://github.com/magicoli/opensim-helpers), the maintained PHP helpers that work with OpenSimSearch (needs PHP on the host), or
  - have the v7 website provide the search service itself (no PHP; same on Windows and Debian). Recommendation, if search is wanted.
  - Either way: the search service reads only public data, is rate limited, and the region's `data_services` registration accepts only known simulators.
  - What V7 has today (the four search pieces, what each covers, and a likely double-module problem on OSGrid): `docs/opensim-search-notes.md` (2026-11-06).
- Ideas from jOpenSim, the old Joomla grid website: public grid status and regions pages, teleport buttons, a panorama, a resident's friends list, self-service privacy requests, simulator stats, money. Each part and where GridGate stands: `docs/jopensim-notes.md` (owner, 2026-11-06).
