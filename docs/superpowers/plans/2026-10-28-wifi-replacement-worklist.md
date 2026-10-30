# Work list — replace Wifi with the v7 website, add languages, remove Wifi

Goal: the launcher website (v7) takes over what the Diva Wifi add-on did. Each feature is redesigned with security first, not copied. The site supports more than one language. Afterwards Wifi is removed from the tree completely. Everything works on Windows and on Debian 12 and 13.

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
- [ ] (staff side done in plan 7a; guest side is plan 7b) Split the site into an **admin area** (avatar level 200 or higher, today's `dg-auth`) and a **resident area** (any confirmed avatar). Use a separate cookie and scheme so a resident session can never reach admin endpoints.
- [x] Roles taken from the Robust `UserLevel`: resident (0+), privileged (for example 50, used for hypergrid links), admin (200+). Make the levels settings, not magic numbers. (admin and supervisor levels as settings; the privileged level 50 for hypergrid is item 2.9, not built in 7a)
- [x] More than one admin: list who has admin level, promote and demote, and refuse to remove the last admin. (The last admin can't be removed: the owner is always an admin and can't be changed.)
- Decided: the resident area is on the **same site and port** as the admin area. Still worth adding: an optional "admin pages only from these networks" allow-list.

### 1.3 Security baseline
- [x] **[S]** Login throttling per avatar name **and** per client IP. Keep the lock state across restarts (store it in the data folder).
- [x] **[S]** Security headers: Content-Security-Policy (no inline script), `X-Content-Type-Options`, `Referrer-Policy`, `frame-ancestors 'none'`.
- [x] **[S]** Audit log for admin actions (who, what, when, from where) in its own file under the logs folder, with no passwords or tokens in it.
- [ ] **[S]** One shared helper for "tokens sent by email": random 256-bit value, only its hash stored, single use, expiry, bound to its purpose.
- [ ] **[S]** Bot protection for public forms (sign-up, forgot password). Every layer has its own on/off switch on an admin settings page:
  - [ ] A1 honeypot field — **on** by default.
  - [ ] A2 rate limit per IP — **on** by default, with an adjustable limit (for example sign-ups per hour).
  - [ ] B proof-of-work puzzle (ALTCHA, self-hosted) — **on** by default, with an adjustable difficulty.
  - [ ] C self-hosted image CAPTCHA — **off** by default; localised, with an audio or text alternative for accessibility.
  - [ ] Admin approval of new accounts — a switch, **off** by default (2.1 and 2.2).
  - [ ] **[S]** Turning a layer off is written to the audit log. The settings page warns when every layer is off and sign-up is open.
- [x] **[S]** Optional TOTP two-factor sign-in for admins. The Robust password stays the first factor.
- [x] Sessions page: an admin sees their active sessions and can end all of them ("sign out everywhere" rotates a per-user stamp).
- [x] Checked the legacy port-8001 routes: `/dreamgrid/regionready` accepts only loopback (tested); the command API on `/` stays reachable from the network on purpose (MachineId or an admin avatar), obeys the admin network list and the throttle, refuses a password alone for two-factor accounts, and audits commands that ran (design 2026-10-30 §7.8).

### 1.4 Email
- [ ] SMTP settings page: host, port, TLS mode, user, password, sender address, and a "send test email" button.
- [ ] **[S]** Store the SMTP password encrypted with data protection. It is never shown back in a page (write-only field).
- [ ] **[X]** Use MailKit (works the same on Windows and Debian) rather than `System.Net.Mail`.
- [ ] Email templates are localised (see 1.5), in plain text and HTML.
- [ ] Every feature that needs email is hidden or explains itself when SMTP isn't set up.

### 1.5 Languages (localisation)
- [ ] Use ASP.NET Core localisation: `IStringLocalizer` and `IViewLocalizer` with `.resx` files per page or area, plus a shared resource for common words.
- [ ] Choose the language from a cookie (the language picker in the header), then the browser's `Accept-Language`, then English.
- [ ] Starting languages: **en, de, es, fr, pt, ru** (the ones Wifi had). Use Wifi's `.po` files as a first draft for translators, then remove them.
- [ ] Dates, numbers and sizes use the chosen culture (`RegionAdvice.FileSize` and friends included).
- [ ] Make the layout work right-to-left (`dir="rtl"`) so Arabic or Hebrew can be added later without a redesign.
- [ ] A test that fails when a key is missing from any language file, or when a page has a hard-coded English string (a scan of `.cshtml` files).
- [ ] **[X]** Make sure `InvariantGlobalization` is **off** and ICU is present: `libicu72` on Debian 12, `libicu76` on Debian 13. Add it to the `.deb` dependencies and test the culture switch on both.
- [ ] Contributor notes: how to add a language (copy the `.resx`, translate, add to the supported list).

## Phase 2 — Wifi features, rebuilt

Each item says what Wifi did, how v7 should do it better, and the security rules.

### 2.1 Resident sign-up (Wifi: `newaccountform`, `avatarselectitem`)
- [ ] Sign-up form: first name, last name, email, password, starter avatar (Female, Male, Neutral, Tiny — the appearance accounts come from settings).
- [ ] **[S]** Password rules (minimum length, check against a common-passwords list), the honeypot and rate limit from 1.3, and the same answer whether or not the name is taken until the email is confirmed (where practical).
- [ ] Modes chosen by the admin: open, email confirmation required, admin approval required, or closed.
- [ ] Copying the starter avatar's outfit and inventory runs as a background job with progress, not inside the request.
- [ ] The viewer's "create account" link (`register=` in `Robust.HG.ini.proto`) points to the new page.

### 2.2 Pending accounts (Wifi: `userpendinglist`)
- [ ] An admin list of accounts waiting for approval: approve or reject, with an email to the user.
- [ ] Pending accounts can't log in to the grid. **[?]** Check how Wifi marked them (a negative `UserLevel`?) and migrate any that already exist.

### 2.3 Password recovery (Wifi: `forgotpasswordform`, `recoveringpassword`)
- [ ] "Forgot password" sends a single-use reset link (30-minute expiry, token hashed when stored).
- [ ] **[S]** Same response whether or not the account exists, rate limited by email and IP, and every session ends after a reset.
- [ ] The viewer's "forgot password" link (`password=` in `Robust.HG.ini.proto`) points to the new page.

### 2.4 Resident account page (Wifi: `useraccountform`)
- [ ] Change email (confirmed by a link sent to the **new** address, with a notice to the old one) and change password (asks for the current password).
- [ ] Choose language and see their own details.
- Decided: residents **cannot** delete their own account. Only an admin can (2.5).

### 2.5 Admin user management (Wifi: `userlist`, `usersearchform`, `usereditform`, `userdeleteform`)
- [ ] Search with paging (no "list everyone" query against a big table), and edit name, email and level.
- [ ] **[S]** Changing an avatar's level is audited. An admin can't raise anyone above their own level or demote the last admin.
- [ ] Delete is two steps: disable first, then a purge after confirmation, with a clear list of what gets removed (inventory, assets left as they are).
- [ ] An admin can set a password, which forces the session out; better, send the user a reset link instead.

### 2.6 Terms of service (Wifi: `termsofservice`, `tos`)
- [ ] The admin edits a ToS text per language, with a version number.
- [ ] Accepted at sign-up. When the version changes, the next website sign-in asks again.
- Decided: **grid login is blocked** until the current version is accepted.
  - Wifi never did this. It only recorded acceptance on the web, in a `TOS` column it added to `GridUser`. Core OpenSim has no ToS check in login.
  - [ ] Add a small DreamGrid patch to `LLLoginService`: when the avatar hasn't accepted the current ToS version, refuse the login with a localised message that names the URL where they can accept it. The patch asks the launcher (loopback only) or reads a launcher-owned table.
  - [ ] Admins (level 200 and above) are never blocked, so the grid can't lock out its owner.
  - [ ] Store acceptance in a launcher-owned table: avatar, ToS version, time, IP. Move existing values from `GridUser.TOS` into it during the upgrade (Phase 3).

### 2.7 Viewer splash page (Wifi: `splash`, `welcome`)
- [ ] A localised welcome page for the viewer's login screen: grid name, online count, region count, news text the admin edits.
- [ ] **[S]** Read-only and cacheable; no admin data in it.

### 2.8 Groups (Wifi: `groupslist`, `groupeditform`, `groupmembers-*`)
- [ ] An admin view of groups: list, rename, change charter, list members, add or remove a member, delete a group.
- [ ] Robust already runs core OpenSim groups (`OpenSim.Addons.Groups.dll`). `1DivaOpenSimServices` is loaded only by Wifi. Its `GroupsService` adds just three admin actions that core OpenSim doesn't offer: rename/charter, delete a group, remove a member. It also has no permission checks of its own.
- [ ] Decided: the launcher does those three actions itself, in one database transaction each, on the core `os_groups_*` tables. Only signed-in admins can use them, and each one goes in the audit log. `1DivaOpenSimServices` is deleted in Phase 3.

### 2.9 Hypergrid links (Wifi: `linkregion*`, `HyperlinkServiceUserLevel`)
- [ ] Privileged users (level setting, default 50) add or remove hypergrid links; admins see all of them.
- [ ] **[S]** Check the address format and allow only http or https; the link test runs with a timeout and never follows redirects to private networks.

### 2.10 Estates (Wifi: `estatelist`, `estateeditform`)
- [ ] List estates, change the owner, add managers, assign regions to an estate. This fits next to the existing Regions pages.

### 2.11 Inventory backups (Wifi: `iars.html`, `inventorylist`)
- [ ] Resident: download their own inventory as an IAR. Admin: load or save an IAR for any avatar.
- [ ] Reuse the OAR library's streamed upload and download pattern (size limits, staging folder, safe names).
- [ ] **[S]** Download links are short-lived and tied to the session; IARs are never served from a guessable path.

### 2.12 Notices to residents — not a Wifi feature
- Wifi has **no** mass email. Its `notification` page is only an on-screen "done / OK" message. Wifi sends just three emails, each to one person: sign-up confirmation, password reset, and "account activated".
- Decided: **not in this work.** Mass email to residents is kept as an idea for later, if wanted (see "Later" at the end).

### 2.13 Not carried over (on purpose)
- [ ] **Web remote console** (`console.html`, `RemoteConsole.html`). Dropped: the launcher already has logs and grid actions. **[?]** Later, maybe an allow-listed command box for admins.
- [ ] **Script engine and custom HTML skins** (`20WifiScriptEngine`, `WifiPages-Custom`). Replaced by a safe theme setting (logo, colours, splash text) with no code from users.
- [ ] **Install form** (`installform`). Already replaced by `/setup`.
- [ ] **Bulk user creation** (`gridusers-creation-progress`). Dropped unless asked for.
- [ ] **Destinations list** (`destinationlist`). Folded into the splash page as "popular regions" if wanted.

## Phase 3 — Remove Wifi completely

Do this only after Phase 2 has shipped the features people use, and after 2.2's migration has run.

- [ ] Remove the projects from `DreamGrid.sln` and delete the folders: `addon-modules/21Wifi`, `20WifiScriptEngine`, `ProcessorTest`, `00DivaInterfaces`, `01DivaUtils`, `02AddinExample`, `00Data` (`Diva.Data`, `.MySQL`, `.SQLite`).
  - [ ] Also delete `1DivaOpenSimServices`, once the launcher's group actions (2.8) work.
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

---

## Suggested order

1. Phase 0 (now).
2. 1.1 HTTPS, 1.3 security baseline, 1.5 languages — so every new page is born secure and translated.
3. 1.2 audiences and roles, 1.4 email.
4. 2.3 password recovery → 2.1 sign-up → 2.2 pending → 2.4 account page → 2.6 ToS → 2.7 splash.
5. 2.5 admin users → 2.8 groups → 2.9 hypergrid → 2.10 estates → 2.11 IAR → 2.12 notices.
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
- Mass email to residents (to everyone or to one group), queued and throttled, with an unsubscribe link. Add it only if it's asked for.
- In-viewer search (places, people, classifieds, events). Keep the `OpenSimSearch` region module (upstream: `kcozens/OpenSimSearch`); it needs a search web service at `SearchURL`. Two ways to provide one:
  - point it at [magicoli/opensim-helpers](https://github.com/magicoli/opensim-helpers), the maintained PHP helpers that work with OpenSimSearch (needs PHP on the host), or
  - have the v7 website provide the search service itself (no PHP; same on Windows and Debian). Recommendation, if search is wanted.
  - Either way: the search service reads only public data, is rate limited, and the region's `data_services` registration accepts only known simulators.
