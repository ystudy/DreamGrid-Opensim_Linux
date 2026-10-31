# Security notes

## Remote consoles

The Robust and region REST consoles listen on ports 8010 (Robust) and 8011 (region) on all network interfaces. OpenSim cannot bind them to localhost without an engine change, so each console uses a dedicated `ConsolePort` and a random 32-character `ConsolePass` generated per install and stored in `settings.json`. The launcher reads that password from there; it is never a fixed default.

Version 1 is LAN-only. The firewall and any router port forwarding must not expose ports 8010 and 8011 to the internet. Anyone who can reach those ports and knows the password can run console commands on the grid.

When the launcher creates the owner avatar it sends `create user` (or `reset user password`) to Robust's REST console, and Robust echoes that command, password included, into its console scrollback (readable only with the console credentials) and into its stdout, which the launcher discards.

Robust's private port (8003) is stock OpenSim and unauthenticated: anyone who reaches it can call the private grid services. Version 1 is LAN-only, so it must not be exposed beyond the LAN either.

## Data directory

The whole data directory is secret, not only `settings.json`. `<data>/settings.json` holds the database passwords, the console passwords and the MachineID, and the INIs the launcher generates under `<data>/runtime/opensim/` and `<data>/state/regions/` carry the same database passwords, the `ConsolePass` and the MachineID. The data directory must be readable only by the account that runs the grid, on both operating systems.

- On Linux the launcher creates `<data>` with mode 0700 and narrows an existing one to 0700 at every start; `settings.json` itself is written with mode 0600.
- On Windows it is `%ProgramData%\DreamGrid`. The installer (plan 3) must restrict the ACL of the whole `%ProgramData%\DreamGrid` tree, not only `settings.json`, to SYSTEM, Administrators and the grid's service account.

## The website is plain HTTP

The website uses plain HTTP, not HTTPS. HTTPS is on the later list (the owner's decision, 2026-10-30). Until then, passwords and two-factor codes cross the network unencrypted. On a LAN that is a small risk. On a public grid it is a real one: anyone on the path can read them. Two-factor stops a stolen password from being enough, but it does not hide the code from someone watching at that moment. Use the allowed networks list (below) to keep the staff side of the website to places you trust. The list does not cover the guest side, which is reachable from any address; see "The guest side".

## Staff levels

Staff are avatars whose level in Robust is high enough.

- A **supervisor** is at level 100 or more. An **admin** is at level 200 or more. Both numbers are settings (`security.supervisorLevel` and `security.adminLevel` in `settings.json`), and a level counts when it is equal to or above the number. The supervisor level must be at least 1, and the admin level must be above it and at most 255. The launcher checks them every time it reads `settings.json`: if they break these rules, it does not start, and the log says which number is wrong. A missing or `null` `security` means the defaults. The launcher reads and writes `settings.json` itself, so stop it before you edit the file by hand, then start it again.
- The **owner** is the avatar that setup made the owner, whatever their level. The owner can never be demoted, suspended or locked out by a setting.
- Admins can use every page except Staff and Security, which are the owner's. Supervisors can only find people on the People page and suspend guests, and open their own Sessions and Two-factor pages.
- Staff page (`/staff`, owner only): lists everyone at supervisor level or above, makes someone a supervisor or admin, or makes them a guest again. The change reaches Robust at once. A person who loses their role is signed out. A person who gets a higher role gets the new rights the next time they sign in; until then they keep the rights they signed in with.
- If Robust takes a change but does not confirm it in time, the page says "The change was sent but your world didn't confirm it." Check the person's level on the People page, then try again if needed. The person is signed out if the change would take rights away, and the audit log records it as `unconfirmed`.
- People page (`/people`, staff): find an avatar by name and see its role, level and email. Suspending sets the level to -1; the old level is remembered in `<data>/security/suspended.json` and comes back when you unsuspend. If that file can't be read, suspending and unsuspending are refused until it can. If it is damaged, it is set aside as `suspended.json.damaged-...` at the next change, and the people it listed get level 0 when unsuspended. The owner can suspend anyone but the owner, an admin can suspend supervisors and guests, and a supervisor can suspend guests. The result also says which regions the person has region rights for ("Region rights: none", or the region names).
- While someone is signed in, the launcher asks Robust for their level again at most every 5 minutes. If Robust does not answer, nobody is signed out for it.
- A Region Server and a Connected Server have no supervisors and no Staff or People pages. The other security pages still apply.
- **After an upgrade:** levels already in Robust count straight away. Any avatar at level 100 to 199 becomes a supervisor (it can use the People page and see e-mail addresses), and any at 200 or more becomes an admin. Check the Staff page after upgrading, and make guests of anyone who should not have a role.

## Sessions

Each staff sign-in is a session. The Sessions page (`/profile/sessions`) lists yours with the time, address and browser. You can end one, or sign out everywhere. Sessions are kept in `<data>/security/sessions.json`, so a restart does not sign anyone out.

## Login throttle

There are two sign-in doors, and each keeps its own count for each avatar name. The staff door is the staff sign-in page, its two-factor code step, the Two-factor page and the port-8001 command API. The guest door is `/guest/sign-in`. Five failures for one avatar name at one door in 10 minutes lock that name at that door for 10 minutes; the other door is not locked. Twenty failures from one address in 10 minutes lock that address for 10 minutes at both doors: an address's failures count together, whichever door they came through. A right password clears the name's count at its own door only. At the staff door it does not clear it while a two-factor code is still to come, and the guest door (which takes the password alone) cannot clear the staff door's count, so neither can be used to reset the count between code guesses. The locks are kept in `<data>/security/throttle.json`, so a restart does not clear them. If that file is damaged the throttle starts empty and the launcher log says so.

Anyone who can reach a door and knows an avatar's name can keep that name locked at that door, the owner's too, by typing five wrong passwords every 10 minutes. The allowed networks list (below) covers the staff side, so once it has entries, nobody outside it can reach the staff door to lock a name there. The guest door is open to every address, so anyone on the internet can lock any avatar name out of the guest door, but that does not lock the name out of the staff door. A locked name opens again by itself after the 10 minutes. If your name is locked and you need in now, stop the launcher, delete `<data>/security/throttle.json`, and start it again. That clears every lock.

## Two-factor sign-in and `reset-two-factor`

Any staff member can turn on two-factor sign-in on the Two-factor page (`/profile/two-factor`). It uses an authenticator app (6-digit codes, a new one every 30 seconds) and gives 10 backup codes. Each backup code works once. A code that was already used is refused. After the password, `/login/code` asks for the code. Everything is kept in `<data>/security/twofactor.json`. Backup codes are stored only as hashes.

The Security page (`/security`, owner only) has the switch "Require two-factor sign-in for admins and supervisors". When it is on, they must set up two-factor the next time they sign in, or when they next open a page. The switch never applies to the owner. The Security page also lists the people with two-factor on and can reset any of them. A reset signs that person out everywhere.

If the owner loses their phone and backup codes, run `reset-two-factor` on the grid computer with the launcher program, the same way the installer runs `admin-link`. It turns two-factor off for the owner. It works whether the grid is running or stopped.

On Debian, run it as the `dreamgrid` account, which owns the data folder:

```
sudo runuser -u dreamgrid -- /opt/dreamgrid/dotnet/dotnet /opt/dreamgrid/launcher/DreamGrid.Launcher.dll reset-two-factor
```

Do not run it as root (plain `sudo`). A file root writes belongs to root, the grid could no longer read it, and every staff sign-in would be refused. So the command refuses to run as any account but the folder's owner, and prints the `runuser` command to use instead.

On Windows, open a Command Prompt with "Run as administrator", then run (`<version>` is the name of the `dotnet-...` folder in `C:\Program Files\DreamGrid`; if there are several, use the newest):

```
"C:\Program Files\DreamGrid\dotnet-<version>\dotnet.exe" "C:\Program Files\DreamGrid\launcher\DreamGrid.Launcher.dll" reset-two-factor
```

It finds the data folder itself (`%ProgramData%\DreamGrid` on Windows, `/var/lib/dreamgrid` on Debian); add `--data <folder>` if the grid keeps its data elsewhere. If it says "This account can't read twofactor.json", it was run as the wrong account: use the command above (on Windows, an Administrator command prompt).

If `twofactor.json` is damaged, two-factor cannot be checked, so the launcher fails closed. While it is damaged, nobody can sign in, the owner included. With two-factor required for staff on, admins and supervisors who are already signed in are sent to the Two-factor page until it is fixed; with the switch off, people who are already signed in are not affected, except someone who signed in while it was on and hasn't set up two-factor yet: they keep being sent to the Two-factor page until they set it up or sign out. Run the same command with `--all-damaged` at the end, as the same account (on Windows, from an Administrator command prompt); it needs no sign-in. On Debian:

```
sudo runuser -u dreamgrid -- /opt/dreamgrid/dotnet/dotnet /opt/dreamgrid/launcher/DreamGrid.Launcher.dll reset-two-factor --all-damaged
```

That sets the damaged file aside (as `twofactor.json.damaged-...`, in the same folder). Everyone then sets two-factor up again.

## Allowed networks (admin network list)

The Security page has a list of allowed networks. Empty means any address may reach the website. Once it has entries, any other address gets an empty 404 response for the staff side of the website: the staff sign-in page, the staff pages, the APIs and the port-8001 command routes. The setup pages, the guest pages and the public pages (the front page, the legal pages, the sign-up page and the bot-protection challenge) are reachable from any address; see "The guest side" below. A staff cookie sent from outside the list is ignored on those pages, so a staff member outside the list is treated like any other visitor there. This computer (loopback) is always allowed, so the owner cannot lock themselves out.

If you change the list so that it no longer covers the address you use, the website answers you with a 404. To get back in, do one of these:

- Sign in from the grid computer itself, at `http://localhost:<web port>/` (the port is 8001 unless you changed it), and fix the list.
- From another computer, open an SSH tunnel to the grid computer, for example `ssh -L 8001:localhost:8001 you@grid-computer`, then open `http://localhost:8001/`. The website sees the tunnel as this computer.
- Stop the launcher, edit `security.adminNetworks` in `<data>/settings.json` (an empty list `[]` allows every address), then start it again.

Do not put a reverse proxy on the same computer in front of DreamGrid. Such a proxy makes every caller look like this computer (loopback), which the list always allows, so the list gives no protection behind it. DreamGrid does not support a reverse proxy.

## Audit log

`audit.log` in the logs folder (`<data>/state/logs`) has one line of JSON for each thing staff did on the website or the port-8001 API, and for each sign-in attempt that is checked (attempts refused by the throttle are not logged). It records the time, who, their role, the address, the action, what it was done to, and how it ended. Staff sign-ins, refused sign-ins, role and suspension changes, two-factor changes, session endings, every change request a signed-in staff member makes, and every port-8001 command that ran and needed a level are in it. It never holds a password, a two-factor code, a backup code, a secret, the MachineId or a token. A refused sign-in names only the first name, or "(an incomplete name)" when no last name was typed (the rest could be a password typed into the name field), except for a staff member whose password was right but whose two-factor file can't be read, or who typed a wrong two-factor code (at sign-in or on the Two-factor page): those are written with the full name. The file rolls by size like the other logs. The owner and admins read it on the Logs page: choose the "audit" source. The guest side adds these: `sign-in` and `sign-in-refused` on the guest door, `legal-text-saved` and `legal-responsibility` from the legal editor, `bot-layer-on`, `bot-layer-off` and `bot-settings` from the Bot protection page, `region-rights-granted` and `region-rights-removed` from a region's page, `estate-move` when DreamGrid moves a region between estates (done by "DreamGrid"), and `region-restart` when a guest restarts a stuck region. Email adds `email-account-saved`, `email-account-removed`, `email-account-moved`, `email-account-paused`, `email-account-resumed`, `email-test-sent`, `email-template-saved` and `email-template-reset`; none of them holds a password, a message or an address.

## Browser security headers

Every response carries a Content-Security-Policy that allows only scripts and styles from the website itself (no inline script or style), and forbids framing the site. It also sends `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin` and `X-Frame-Options: DENY`. Nothing is loaded from another origin.

## Port-8001 command API and two-factor

The patched OpenSim and Robust, and the scripts on this computer, send commands to the website on the port-8001 routes with an avatar name and password, or with the MachineId. An avatar's password is one factor only. So:

- An avatar with two-factor on must use the MachineId.
- Any admin or supervisor must use the MachineId while "two-factor required for staff" is on. The owner is not refused by the switch.
- If two-factor cannot be checked (the file is damaged), avatars are refused and only the MachineId works.
- If Robust does not answer, only the MachineId works.

The MachineId is a secret in `settings.json`. Treat it like a password and never post it.

## The guest side

Plan 7b adds a side of the website for the people who play on the grid. It exists on Local, Simple Public and Grid Asset Server. A Region Server and a Connected Server have none of it: every guest page, the legal editor, the Bot protection page and the bot-protection challenge answer 404, and `/` behaves as before.

- **Front page** (`/`): anyone who is not signed in as staff sees it (the address stays `/`; the page is `/home`). It has links to sign in and to create an account, and a small "Staff sign in" link. A signed-in staff member who opens `/` goes to their own start page as before.
- **Guest sign-in** (`/guest/sign-in`): the avatar name and password of any account at level 0 or more. A staff member can use it too, but it opens only the guest pages. It asks for no two-factor code, because it opens nothing on the staff side. A wrong name, a wrong password and a suspended account all show the same message ("Name or password is wrong."). The guest door has its own count for each avatar name (see "Login throttle"): five wrong passwords for one name lock that name at the guest door only, and a right password here does not clear the staff door's count. Its failures count toward the same limit for each address as the staff door's. The password is never logged; the log and the audit log name only the cleaned first name of a refused sign-in.
- **Guest pages**: `/guest` (the steps for connecting with Firestorm, and a link to My regions for someone who has region rights), `/guest/terms`, `/guest/regions` and `/guest/sign-out` (a form button, not a link).
- **Cookie**: `dg-guest` holds the guest's sign-in. It is a session cookie (gone when the browser closes), HttpOnly, SameSite Strict, and ends after 8 hours without use. It opens only guest pages, and the staff cookie `dg-auth` opens no guest page. Signing out of one door leaves the other alone. Every cookie the website sets is listed in the legal editor (see below) and in `Launcher/DreamGrid.Launcher/Guests/SiteFacts.cs`.
- **Suspension**: suspending someone on the People page ends their guest sessions at their next request, not at the next level check. Each guest has a stamp in `<data>/guests/stamps.json`, and suspending changes it. The launcher also asks Robust for a guest's level again at most every 5 minutes, and a guest whose account is gone or below level 0 is signed out. If Robust does not answer, nobody is signed out for it.
- **The allowed networks list** covers only the staff side (see "Allowed networks"). Guest and public pages are reachable from any address, because guests come from anywhere. So the list no longer hides that a website is there: `/`, `/guest/sign-in`, `/terms` and the rest answer from any address. It still hides the staff sign-in, the staff pages and the APIs.
- **Plain HTTP**: guest passwords cross the network unencrypted, just like staff passwords (see "The website is plain HTTP"). On a public grid the legal editor and the dashboard say so at the top.
- **Terms**: once the operator has written Terms, a signed-in guest must accept the current version on `/guest/terms` before any other guest page (the Terms page, sign-in and sign-out excepted). The acceptance records the avatar, the version, the time and the guest's IP address in `<data>/guests/terms.json`. Every acceptance is kept, newest last. When the operator changes the Terms text, the version goes up and everyone is asked again.
- **Email tokens**: a helper for links sent by email (`Launcher/DreamGrid.Launcher.Core/Guests/EmailTokens.cs`, file `<data>/guests/email-tokens.json`) is built, but nothing uses it yet: this release has no email sending, password recovery or sign-up. A token is 256 random bits shown once. Only its SHA-256 hash is stored, with its purpose, the account and an expiry. It works once, for that purpose and that account.
- **Sign-up is not open**: `/signup` says "This grid isn't open for sign-ups yet." Accounts are still made by setup and by admins.

## Legal pages and the sign-up gate (DreamGrid provides no wording)

DreamGrid ships no Terms, Privacy, Cookies or Contact text, and no template or example for one. What your grid needs to tell its guests depends on where you are and what you do with their data, and only you can decide that. DreamGrid supplies the places to put the pages and some facts about the software.

- **The owner's editor** is the Security page's link "Write the Terms, Privacy, Cookies and Contact pages", at `/security/legal` (page title "Legal pages"). It is the owner's, like the Security page. There is one box for each of Terms, Privacy, Cookies and Contact (Contact is optional). Write in Markdown. Raw HTML in the text is switched off and links that are not safe are dropped. Leave a box empty to remove that page.
- **Save** puts the pages live at once. A page whose text changed gets the next version number and the time; a page you did not change keeps its version. **Preview** shows how the pages will look and saves nothing. Visitors read the pages at `/terms`, `/privacy`, `/cookies` and `/contact`. The saves are audited (`legal-text-saved`).
- **Versions**: a page that you clear and later write again gets a new number, never one that someone already accepted. Changing the Terms asks every guest to accept them again.
- **The responsibility tick**: "I wrote these notices and I am responsible for them." is its own small form with its own Save button, and the audit log records it (`legal-responsibility`). DreamGrid does not check what you wrote, and the tick does not make the text correct or complete.
- **The gate**: sign-ups can be opened only when Terms, Privacy and Cookies all have text and the tick is on. The editor says "Sign-ups can be opened", or "Before sign-ups can be opened:" and what is still missing. Nothing opens sign-ups yet; the gate is there for when a sign-up form exists.
- **"Facts about this software, not legal text"** is a box on the editor page. It lists the cookies this website sets and what it stores about people and where, so the person writing the Cookies and Privacy pages does not have to guess. A test proves that every cookie the website sets is on the list. It is not advice, and it knows nothing about your hosting, other services you run or the laws that apply to you. Check it against how you run the grid.
- **The plain-HTTP warning** appears at the top of the editor, and of the dashboard, on a public grid reached over plain HTTP. Passwords typed on the guest pages cross the internet unencrypted until HTTPS is built.
- The legal texts are kept in `settings.json` (under `legal`), not in `<data>/guests`.

## Region owners and estates

An admin can give one guest the rights over one region. This is for a guest who runs their own part of the grid.

- **Giving and removing rights**: on a region's page (Regions, then the region) the "Region owner" section has an avatar name box and "Give region rights". The name must be an existing avatar. Giving the rights to someone else replaces the earlier owner. "Remove region rights" asks first when the browser's JavaScript is on (without it, the button acts at once). Both are audited (`region-rights-granted`, `region-rights-removed`); a handover to someone else writes `region-rights-removed` for the earlier owner, then `region-rights-granted`. The Regions list shows "Owner: <name>" under the region's name, and the People page shows each person's region rights.
- **Estates**: when the region runs, DreamGrid moves it into the guest's own estate, "<First Last> Estate", using OpenSim console commands. The estate is made the first time one of that guest's regions moves, and their later regions go into it. A background check looks for regions that have to move every 60 seconds. A region that is not running waits, and a move that fails is tried again every minute (the log says so once). A move is audited as `estate-move`, done by "DreamGrid".
- **Taking rights away** moves the region back to the estate it came from the next time it runs. The guest's estate is kept: DreamGrid never deletes an estate. Taking away a right whose region never moved just drops the right.
- **What the guest can do**: `/guest/regions` (page title "My regions", linked from the guest's account page) lists the guest's regions with their state, how many people are in them and when they last started. Normal restarts are done in-world from the viewer's Region/Estate window. The page offers a restart only for a stuck region: one that is not responding (it restarts) or one that is stopped while the grid service runs (it starts). A region can be restarted this way at most once every 10 minutes, and only when every region it runs with is the guest's own. The page says why a restart is refused: after a recent restart it says when to try again (a time in UTC), and for a region that runs with regions that are not the guest's it says to contact the grid's staff. The server checks all of this again when the form is posted, whatever the form said, and a post for a region that is not theirs gets 403. Each try is audited as `region-restart`. The 10-minute record is kept in memory, so restarting the launcher lets an owner restart once more.
- **A region that runs with other regions**: its page shows the regions it runs with and a button to let it run on its own. That restarts the other regions in the same program too, so the page asks first (on the server, so it holds without JavaScript).
- **Region Server and Connected Server** have none of this. Region owners for regions on a Region Server are on the later list.
- **If `region-rights.json` is damaged**, see the last section.

## Bot protection

The owner's page `/security/bots` (page title "Bot protection"; the Security page links to it as "Protect sign-up from bots") switches the layers that guard forms anyone on the internet can send. Sign-up is not open, so today the only such form is "Try it" on this page. The layers will guard the sign-up form when it exists. Each layer has a switch. The defaults:

- **Hidden field (honeypot)**: on. A field people never see; a program that fills in every field gives itself away.
- **Limit sign-ups from one address**: on, 5 posts an hour for each address and form (1 to 1000). The count is in memory, so a restart starts it again. A post counts only when every layer passed.
- **Proof of work**: on, difficulty 50000 (1000 to 1000000). The visitor's browser does a small calculation before the form is sent, with a script served from the website itself (`/js/pow.js`; nothing is loaded from another site). The challenge comes from `/api/bots/challenge`, which is open to anyone. Each solved challenge is accepted once. Challenges are signed with a key in `<data>/security/pow.key`, made on first use; if that file is lost or damaged a new key is made, and only the challenges already handed out stop working. A browser with JavaScript off cannot solve it.
- **Picture puzzle (image CAPTCHA)**: off. The visitor types the letters in a picture, or answers a sum written in words ("What is four plus seven?"), which is the text alternative for people who cannot see the picture. Either answer passes, and a challenge gets one try. It is made on this computer, with no outside service. There is no audio alternative yet. The sum in words can be answered by any program that reads English, so this layer mainly stops scripts that don't read the page.
- **An admin approves each new account**: off. The switch is stored and audited, but nothing uses it until sign-ups exist.
- **Try it** sends a test form through the layers that are on. It creates nothing, and each try counts toward the limit for the address.

The page warns when every layer is off. Turning a layer on or off, and changing the limit, the difficulty or admin approval, is written to the audit log (`bot-layer-on`, `bot-layer-off`, `bot-settings`). A stopped post logs the layer and the form only, never what was typed, and no answer, proof or key is ever logged. These layers slow down programs. They do not stop a person who is paid to answer them, so keep the other protections on.

## Email

The owner sets up email on the Email page (`/security/email`, owner only, not on a Region Server or a Connected Server). Each card is a free mail service, or Other server for any other. More than one account can be set up: the grid sends with the first one in the list that is under its hourly and daily limits, and moves to the next when one is full or stops working.

- **Passwords** (app passwords, SMTP keys) are kept in `<data>/email/accounts.json`, encrypted with the website's data-protection keys (`<data>/keys`). They are never shown again, never logged and never in the audit log. Removing an account deletes its password.
- **Waiting emails** are kept in `<data>/email/outbox.json` until they are sent or expire, so a restart doesn't lose them. The address and the message (with any reset or confirmation link) are encrypted the same way.
- **Recent problems** (`<data>/email/problems.json`, 7 days) say when an email couldn't be sent, which email, for which avatar and why. No address, message or link.
- **Send counts** (`<data>/email/sent.json`) are the send times of the last 24 hours, by account.
- The named cards always use an encrypted connection and check the server's certificate. Other server can choose "None" only for a mail server on your own network.
- **Backups:** a copy of the whole data folder holds the data-protection keys too, so it can decrypt the email passwords and the two-factor secrets. Keep data-folder backups private, like the grid computer itself.
- After the keys are replaced, saved passwords can't be read: the cards say "Needs attention", and typing the password again fixes it. Waiting emails that can't be read become recent problems.

## What lives in `<data>/guests` and how to recover a damaged file

Everything the guest side stores is under `<data>/guests` (`/var/lib/dreamgrid/guests` on Debian, `%ProgramData%\DreamGrid\guests` on Windows). Like the rest of `<data>` it must be readable only by the account that runs the grid, and it is never in the Robust or OpenSim databases. The legal texts and the bot settings are in `settings.json`, and the proof-of-work key is `<data>/security/pow.key`.

| File | What it holds | If it is missing or damaged |
| --- | --- | --- |
| `stamps.json` | Each guest's sign-in stamp (changed by a suspension) | Every stamp goes back to the starting value. A guest whose cookie carries a changed stamp signs in again; the others stay signed in. A suspended guest is still ended by the 5-minute level check. |
| `terms.json` | Each acceptance of the Terms: avatar, version, time and IP address | Nobody has accepted, so everyone is asked to accept the Terms again. The record of earlier acceptances is lost unless you keep the set-aside file. |
| `region-rights.json` | Region rights, each guest's estate and the estate each moved region came from | Nobody has any region rights. Regions already in a guest's estate stay there. DreamGrid no longer knows those estates or where the regions came from: if an admin gives the rights again, DreamGrid remembers the estate the region is in now (the guest's old one) as the one it came from, makes a second estate with the same "<First Last> Estate" name, and moves the region into it. Taking the right away later sends the region back to the guest's old estate, not to the grid's. Check the estates in the viewer, or with `estate show` on the region's console, and move the region by hand if it should be somewhere else. |
| `email-tokens.json` | Hashes of single-use email tokens | No token is valid. Nothing uses them yet. |

A file that is not valid JSON is moved aside as `<name>.damaged-<UTC yyyyMMddHHmmss>` in the same folder when the launcher reads it, so the next save does not destroy it. The log says so once, with the file's path, and the launcher goes on as if the file were empty. A file that cannot be opened at all (permissions, something in the way) is not moved aside: the log says so and the launcher treats it as empty, and the next change saved to that file replaces it, so what it held is lost. When you see that warning, stop the launcher at once, fix the permissions, and start it again. A file that cannot be written is logged once, and the changes are kept in memory only until it can be.

To recover:

1. Stop the launcher and find the `*.damaged-*` file next to where the file belongs.
2. If you can repair it by hand (it is plain JSON), copy the repaired content over the real file name while the launcher is stopped, then start the launcher. If you cannot, leave it: the grid works with the file empty, as the table says.
3. Delete the set-aside file when you no longer need it. Keep a set-aside `terms.json` if you need the record of who accepted what.

Do not edit these files while the launcher runs. It writes them whole and would overwrite your change.

## Check after updating

DreamGrid's automated tests cannot see these in a real browser. They are still open. After updating, the owner should check:

- [ ] Open the signed-in pages and the guest pages in a browser with its developer console open (F12, then Console), and look for messages that start with "Refused to" or mention the Content-Security-Policy. Pages to open: `/` (signed out), `/guest/sign-in`, `/guest`, `/guest/terms`, `/guest/regions`, and as staff `/security`, `/security/legal`, `/security/bots`, the Regions list and a region's page. There should be none.
- [ ] On the Bot protection page press "Try it" with the layers you use switched on, and once with the picture puzzle on. It should say the test passed. If guests will use slow phones, try it on one and see that the wait is acceptable.
