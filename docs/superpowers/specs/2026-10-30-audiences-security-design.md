# Guests, staff and the security baseline — design

Phases 1.2 (two audiences) and 1.3 (security baseline) of `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`, worked out with the owner on 2026-10-30. Marked **(owner)** where the owner decided, **(my call)** where the designer did; everything else is left to the builder. The worklist items for 1.2 and 1.3 stay binding where this document doesn't change them.

## 1. Why

The launcher website today has one kind of user: an administrator (`dg-auth`, level 200 or more, `AdminRule`). Wifi's replacement (Phase 2) needs guests who register and sign in on the same site, and grids run by more than one person. Every Phase 2 feature — sign-up, approval, password reset, terms, user admin — assumes guests and staff are kept apart, sign-ins are throttled, and staff actions are logged. This design builds that foundation, plus the legal pages a grid open to the public needs.

**(owner)** DreamGrid is open source with no budget: nothing here may depend on a paid or registered outside service (no SMS, no hosted CAPTCHA, no CDN).

## 2. Who uses the website

| Who | How they're identified | What they do |
|---|---|---|
| **Guest** | Any account on this grid that can log in (OpenSim `UserLevel` ≥ 0) | Sign in on the guest side; accept the current Terms; see how to connect with Firestorm. Later (Phase 2): register, verify, manage their account. |
| **Region owner** | A guest an admin has given rights to one or more named regions | Everything a guest does, plus a **My regions** section (§5). **(owner)** "Guest X pays me for a private region, I give them region rights." |
| **Supervisor** | Level ≥ the supervisor level (setting, default **100** — my call) and below the admin level | Looks after people, not the machine: find an account, suspend and unsuspend guests; approve sign-ups once 2.2 exists. No grid settings, no regions, no staff changes. |
| **Admin** | Level ≥ the admin level (setting, default **200**, today's `AdminRule.AdminLevel`) | Runs the grid: start/stop, regions, settings, backups, accounts, region rights. Everything the website does today. |
| **Owner** | The avatar created in setup (`Setup.Owner`) | Everything; the only one who makes and removes admins and supervisors, edits the legal pages and the security switches. Can never be demoted, suspended or locked out by anyone else. |

- **(owner)** Several admins and supervisors per grid: people who download DreamGrid run their own grid and must be able to bring in helpers.
- Levels are settings, not magic numbers (worklist 1.2). The existing "privileged" level for hypergrid (50) is unchanged and is not a staff role.
- **(my call)** Supervisor and admin levels are compared with `>=`; an avatar whose level is at or above the admin level is an admin. The owner is the owner whatever their level.
- **(my call)** The grid can never be left without someone able to run it: the owner can't be demoted, and a level change that would leave no owner-reachable admin is refused (in practice the owner always remains).

### 2.1 Which plans have which people

**(owner)** The Terms live wherever guests register, i.e. on the machine that keeps the accounts.

| Plan | Guest side, legal pages, region owners | Staff |
|---|---|---|
| Local, Simple Public, Grid Asset Server | Yes | Owner, admins, supervisors |
| Region Server | **No** (accounts and Terms are on the Grid Asset Server) | Today's rule unchanged: the owner named in setup, or level ≥ admin on the main grid. No supervisors, no staff page. |
| Connected Server | **No** (accounts are on the other grid) | Today's rule unchanged: only the owner named in setup. |

On Region Server and Connected Server the website stays admin-only; the guest-side URLs answer "not found".

**(my call)** The security baseline (§7) applies on **every** plan: throttling, two-factor, the sessions page, the audit log, the allow-list and the browser protections. On Region Server and Connected Server the owner named in setup still has an owner-only **Security** page (the two-factor switch, two-factor resets for the admins who sign in there, the allow-list, the bot-protection switches are hidden since there are no public forms); only the Staff page and the legal pages are missing. The owner's own reset is the command-line verb, as everywhere. **(my call)** Region owners on a Grid Asset Server apply only to regions that run on that machine; renting out a region that runs on a Region Server is later work (§12).

## 3. Two doors (worklist 1.2)

- **Separate schemes and cookies.** Staff keep the existing admin scheme and cookie (`dg-auth`); guests get their own scheme and cookie. A guest cookie never satisfies a staff policy, on pages or on `/api/*`. The current deny-by-default fallback policy (admin) stays.
- **Policies:** *owner*, *admin* (admin + owner), *staff* (supervisor + admin + owner), *guest*. Existing admin pages keep the admin policy; the new people pages (§6) take *staff* where a supervisor may use them.
- **Sign-in:** staff sign in at the existing `/login`; guests at a new guest sign-in page. **(my call)** A staff member who wants the guest pages signs in there separately; one sign-in never grants the other side.
- **Front page (my call):** a GET of `/` by someone not signed in as staff shows a public front page — grid name, guest sign-in, links to the legal pages, a small "Staff sign in" link — instead of redirecting to `/login`. The dashboard stays reachable at `/dashboard` (already a route), and a signed-in staff member going to `/` gets the dashboard as today. The port-8001 contract on `/` (`alt`/`type`/`command`, POST) is unchanged. Setup's gate (`SetupGate`) still comes first. On Region Server and Connected Server `/` behaves as today.
- **Same site and port** for both sides (worklist, decided).
- **Admin network allow-list (my call):** an optional list of networks (CIDR) allowed to reach the staff door and staff pages, **off** by default. Loopback is always allowed, so the owner can't lock themselves out from the grid computer.
- **Level changes take effect:** **(my call)** a staff member's level is checked again against Robust at sign-in and at most every 5 minutes while signed in (when Robust is reachable); a demoted or suspended person's sessions end. A demotion made on the website ends that person's sessions at once.

## 4. Legal pages

**(owner)** DreamGrid ships **no legal text** and no fill-in template: the operator is responsible for their notices and writes or obtains their own, then pastes them in. DreamGrid must not look like it gives legal advice.

- **Pages:** Terms, Privacy, Cookies, and an optional Contact / "who runs this grid" page. Public URLs, linked from the front page footer and every guest page.
- **Editor (owner only):** one text per page per language. Until 1.5 adds languages there is one (the default language); the storage is keyed by language from the start. **(my call)** The text is Markdown with raw HTML switched off, so pasted text can't carry scripts.
- **Versions:** saving a changed Terms or Privacy text gives it a new version number. A guest who hasn't accepted the current Terms version is shown it, and asked to accept, the next time they sign in on the website; acceptance is recorded (avatar, version, time, IP — worklist 2.6). The viewer-side refusal patch stays in 2.6.
- **The gate (owner):** guest sign-up can't be switched on until Terms, Privacy and Cookies each have text in the default language and the owner has ticked "I wrote these notices and I am responsible for them." Until then, a sign-up link shows "This grid isn't open for sign-ups yet." The switch itself arrives with 2.1; this design provides the gate and the state it checks. Admins can always sign in and run the grid.
- **Facts beside the editor (owner approved):** a plain list, labelled "Facts about this software, not legal text": every cookie the site sets and what it's for, and what the software stores about people (avatar name, email, sign-in times, IP addresses in the audit and throttle records, Terms acceptances) and where. The list is generated from, or tested against, the real cookie names so it can't drift.
- **No tracking cookies (my call):** the site sets only cookies it needs to work (sign-in, antiforgery, language once 1.5 lands). This is what lets the operator get by without an "accept cookies" banner in most places; the Cookies page is the operator's to write.

## 5. Region owners

- **Granting:** on a region's page an admin enters an avatar name to give that guest rights to the region; removing is one click. Both are written to the audit log. One region can have one region owner. **(my call)** A region with a region owner is shown as such in the regions list.
- **Estate (my call):** today every region is in one estate owned by the grid owner (`[Estates] DefaultEstateName = "<grid> Estate"`). Granting rights moves the region into **one estate per region owner**, owned by the guest, so in-world they get the normal estate tools (bans, access, terrain, the viewer's Restart Region), and a guest who rents three regions sets their bans and access list once.
  - The estate is created the first time that guest is given a region, named "<First Last> Estate" (with a number added if that name is taken; OpenSim estate names must be unique), and reused for every later region.
  - Removing rights moves the region back to the grid's estate. The guest's estate is **kept** even when it's empty, with its bans, access list and covenant, so giving them a region again restores their settings. It's never deleted automatically.
  - The mechanism (OpenSim's `estate` console commands or the launcher's own SQL on the `opensim` database) is the builder's call; it must work while the region is running, and a region keeps its estate when it moves to another program ("Runs with").
- **My regions (owner):** for each of their regions — status (running, stopped, starting), people on it now, when it last (re)started.
- **Stuck-region restart (owner):** a restart button that appears only when the region isn't responding (not running when it should be, or failing its health check). Normal restarts are done in-world from the viewer's Region/Estate window. Each restart is written to the audit log and rate-limited (**my call**: one per 10 minutes per region).
- **Shared programs (my call):** regions can share one simulator program (`RegionSettings.Instance`); restarting it drops every region on it. A region owner's restart is offered only when every region on that program belongs to them; otherwise the page says to contact the grid's staff. When an admin grants rights to a region that shares a program, the page says so and offers to give the region its own program (the existing "Runs with" action).
- **Not for region owners (owner + my call):** changing region settings, uploading or downloading backups (OAR download is later work, §12), anything touching other regions.

## 6. People pages (staff)

Kept small; the full user list, edit and delete stay in 2.5.

- **Staff (owner only):** who has supervisor or admin level, with promote, demote and remove. Changes the account's `UserLevel` through Robust's console (`set user level`, as setup already does). Refuses anything that touches the owner.
- **Find an account (staff):** by avatar name: level, role, created, last login, region rights.
- **Suspend / unsuspend (staff):** **(my call)** suspending sets the account's level below OpenSim's minimum login level (−1) and remembers its previous level so unsuspend restores it; the person's website sessions end. A supervisor can suspend guests and region owners only; an admin can also suspend supervisors; nobody can suspend the owner or an admin except the owner.

## 7. Security baseline (worklist 1.3)

### 7.1 Sign-in throttling
- Extend `LoginThrottle` to key on the avatar name **and** the client IP. Name: today's 5 failures in 10 minutes → 10-minute lock. **(my call)** IP: 20 failures in 10 minutes → 10-minute lock.
- Applies to both doors, the two-factor code step, and the port-8001 API sign-in it already shares.
- **Lock state survives a restart:** stored in the owner-only data folder.
- The message stays generic and never reveals which names exist.

### 7.2 Two-factor sign-in for staff
- **(owner)** Free and offline: authenticator-app codes (TOTP, RFC 6238: 6 digits, 30-second steps, ±1 step allowed, a used code can't be reused). Works with any authenticator app. No SMS, no outside service.
- Set up from the staff member's own account page: QR code generated by the launcher (no outside image service), confirm with one code, then shown **10 one-time backup codes** (only their hashes are kept). The Robust password stays the first factor.
- The secret is encrypted with the site's data-protection keys (already in the data folder).
- **(owner)** The owner can switch "two-factor required for staff" on and off. While on, an admin or supervisor without two-factor is sent to set it up before anything else. **(my call)** The switch doesn't apply to the owner, who decides for themselves, so the owner can't lock themselves out with it.
- **Resets:** the owner can reset an admin's or supervisor's two-factor from the Staff page. The owner resets their own with a new command-line verb run on the grid computer (alongside `admin-link`, `stop-grid`), so only someone with access to that machine can.
- Turning the requirement off, and every reset, is written to the audit log.

### 7.3 Sessions page
- A staff member sees their own active sessions (started, last seen, IP, browser) and can end one or **sign out everywhere**, which rotates their per-person stamp. The cookie carries the session id and stamp; both are checked on each request.
- **(my call)** Guests get the same "sign out everywhere" on their account page later (2.4); the stamp mechanism is shared.

### 7.4 Audit log
- Its own file under the logs folder (`audit.log`), rotated like `launcher.log`. One line per action: time (UTC), who (name and account id), their role, IP, action, target, result.
- Covers every staff action, every region-owner restart, region-rights changes, level changes, suspensions, two-factor changes, security-switch changes, legal-text saves, and sign-ins (success and failure, by first name only as today).
- Never contains passwords, codes, tokens or secrets. **(my call)** Admins and the owner can read it on the Logs page; supervisors can't.

### 7.5 Email tokens (helper only)
One shared helper for links sent by email (worklist): 256-bit random value, only its hash stored, single use, expiry, bound to its purpose and account. Built and tested here; its users (sign-up confirmation, password reset) arrive with 1.4 and Phase 2.

### 7.6 Bot protection
As the worklist lists it: A1 honeypot (on), A2 per-IP rate limit (on, adjustable), B self-hosted proof-of-work puzzle (ALTCHA, on, adjustable difficulty), C self-hosted image CAPTCHA (off; localised; audio or text alternative), admin approval switch (off). Each has its own switch on an owner settings page; turning a layer off is audited; the page warns when every layer is off and sign-up is open. Built as one guard any public form can use; the forms that use it arrive in Phase 2. **(my call)** C may be the last piece built, since nothing turns it on by default.

### 7.7 Browser protections
`Content-Security-Policy` with no inline script and `frame-ancestors 'none'`, plus `X-Content-Type-Options: nosniff` and `Referrer-Policy` (worklist). Pages that use inline script today move it to files under `wwwroot`. No outside origins are allowed (nothing is loaded from a CDN).

### 7.8 Port-8001 legacy routes
The worklist assumed both legacy routes are limited to loopback or simulator addresses. Checked 2026-10-30: only `/dreamgrid/regionready` is (loopback, `LegacyEndpoints.IsLoopbackCaller`); the command API on `/` is reachable over the network on purpose (v1 spec §5: avatar name and password with level ≥ 200, or the MachineId), and no "simulator addresses" setting exists.
- `/dreamgrid/regionready`: stays loopback-only; add tests that prove a non-loopback caller is refused.
- **Command API on `/` (my call):** stays network-reachable so existing LAN scripts keep working, but it may not become a way around the staff door:
  - it obeys the admin network allow-list (§3) when that is on;
  - avatar sign-ins use the same throttle by name and IP (§7.1);
  - an avatar with two-factor turned on is refused here with its password alone (there's no code field), and so is any admin or supervisor while "two-factor required for staff" is on; scripts use the MachineId instead;
  - every command that changes something is written to the audit log.

  Tests cover each of these.

## 8. Where things are kept

- **(my call)** Everything the staff side needs to let someone in — throttle state, staff sessions and stamps, two-factor secrets and backup-code hashes, the security switches — lives in the owner-only data folder, not in MariaDB, because the admin pages must work while the grid (and its database) is stopped.
- **(my call)** Guest-side records — Terms acceptances, region rights, email tokens, suspended accounts' previous levels — also live in the owner-only data folder, next to `settings.json`, not in the `robust` or `opensim` databases. The launcher has no whole-grid backup feature today; keeping everything the launcher owns in the data folder means copying that folder (plus the databases) is the whole backup, and a future backup feature has one place to take it from. The file format is the builder's call (it must cope with thousands of guests, survive restarts and upgrades, and never be half-written after a power cut).
- Settings (levels, switches, allow-list, legal texts and versions) follow the existing settings conventions; new settings fields must upgrade old `settings.json` files cleanly (schema version bump).

## 9. Goals, non-goals, anti-goals

**Goals:** guests and staff kept apart; several staff with three levels; region owners with a status view and a stuck-region restart; operator-written legal pages with a sign-up gate; the full 1.3 baseline; all on Windows, Debian 12 and Debian 13.

**Non-goals (here):** guest sign-up, verification, password reset, account page, full user admin (Phase 2); email sending (1.4); languages (1.5); HTTPS (shelved); the viewer-side Terms check (2.6).

**Anti-goals:**
- Any legal wording shipped by DreamGrid.
- Any paid or registered outside service.
- A guest session reaching anything on the staff side.
- An owner locked out of their own grid by a setting, a demotion, or two-factor.
- Secrets in any log.

## 10. Plain HTTP on public grids

**(owner, 2026-10-30)** HTTPS stays on the later list; it isn't tackled now. The guest side works over plain HTTP on every plan that keeps accounts, including Simple Public Server and a public Grid Asset Server, knowing that guests' passwords then cross the internet unencrypted.

**(my call)** On a public plan (a public address in the network step) the owner and admins see a short, plain warning while the site is plain HTTP: on the dashboard, and on the legal-pages gate, since that's where the owner decides to open the grid to the public. It says what the risk is, not how to fix it with outside services. Cookies get `Secure` automatically once HTTPS exists; nothing here may stop that from being added later.

## 11. Testing

Unit tests for every rule above (roles by level, owner protection, throttling by name and IP and its persistence, TOTP including reuse and clock skew, backup codes, stamp rotation, the legal gate, region-owner restart eligibility, the email-token helper, audit lines carrying no secrets). Integration tests through the real website for: a guest cookie refused on staff pages and APIs; a supervisor refused on admin pages; the staff page refusing changes to the owner; two-factor required by the switch; the security headers on every response; the port-8001 address checks. Debian CI runs them as today.

## 12. Later (not in this design)

- Advanced install: let the owner choose the database password instead of a generated one. **(owner)**
- Region owners for regions on a Region Server.
- OAR download for region owners (needs a permissions-safe export).
- HTTPS for the website **(owner: later, not now)**; the shelved notes are in `2026-10-29-https-design.md`.
- The public WAN website.
