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

The website uses plain HTTP, not HTTPS. HTTPS is on the later list (the owner's decision, 2026-10-30). Until then, passwords and two-factor codes cross the network unencrypted. On a LAN that is a small risk. On a public grid it is a real one: anyone on the path can read them. Two-factor stops a stolen password from being enough, but it does not hide the code from someone watching at that moment. Use the allowed networks list (below) to keep the website to places you trust.

## Staff levels

Staff are avatars whose level in Robust is high enough.

- A **supervisor** is at level 100 or more. An **admin** is at level 200 or more. Both numbers are settings (`security.supervisorLevel` and `security.adminLevel` in `settings.json`), and a level counts when it is equal to or above the number. The supervisor level must be at least 1, and the admin level must be above it and at most 255. The launcher reads and writes `settings.json` itself, so stop it before you edit the file by hand, then start it again.
- The **owner** is the avatar that setup made the owner, whatever their level. The owner can never be demoted, suspended or locked out by a setting.
- Admins can use every page except Staff and Security, which are the owner's. Supervisors can only find people on the People page and suspend guests, and open their own Sessions and Two-factor pages.
- Staff page (`/staff`, owner only): lists everyone at supervisor level or above, makes someone a supervisor or admin, or makes them a guest again. The change reaches Robust at once. A person who loses their role is signed out.
- People page (`/people`, staff): find an avatar by name and see its role, level and email. Suspending sets the level to -1; the old level is remembered in `<data>/security/suspended.json` and comes back when you unsuspend. The owner can suspend anyone but the owner, an admin can suspend supervisors and guests, and a supervisor can suspend guests.
- While someone is signed in, the launcher asks Robust for their level again at most every 5 minutes. If Robust does not answer, nobody is signed out for it.
- A Region Server and a Connected Server have no supervisors and no Staff or People pages. The other security pages still apply.

## Sessions

Each staff sign-in is a session. The Sessions page (`/profile/sessions`) lists yours with the time, address and browser. You can end one, or sign out everywhere. Sessions are kept in `<data>/security/sessions.json`, so a restart does not sign anyone out.

## Login throttle

Five failed sign-ins for one avatar name in 10 minutes lock that name for 10 minutes. Twenty failed sign-ins from one address in 10 minutes lock that address for 10 minutes. The locks are kept in `<data>/security/throttle.json`, so a restart does not clear them. If that file is damaged the throttle starts empty and the launcher log says so.

## Two-factor sign-in and `reset-two-factor`

Any staff member can turn on two-factor sign-in on the Two-factor page (`/profile/two-factor`). It uses an authenticator app (6-digit codes, a new one every 30 seconds) and gives 10 backup codes. Each backup code works once. A code that was already used is refused. After the password, `/login/code` asks for the code. Everything is kept in `<data>/security/twofactor.json`. Backup codes are stored only as hashes.

The Security page (`/security`, owner only) has the switch "Require two-factor sign-in for admins and supervisors". When it is on, they must set up two-factor the next time they sign in, or when they next open a page. The switch never applies to the owner. The Security page also lists the people with two-factor on and can reset any of them. A reset signs that person out everywhere.

If the owner loses their phone and backup codes, run this on the grid computer, with the launcher program (the same way the installer runs `admin-link`):

```
reset-two-factor
```

It turns two-factor off for the owner. It finds the data folder itself (`%ProgramData%\DreamGrid` on Windows, `/var/lib/dreamgrid` on Debian); add `--data <folder>` if the grid keeps its data elsewhere. It works whether the grid is running or stopped.

If `twofactor.json` is damaged, two-factor cannot be checked, so the launcher fails closed. While it is damaged, nobody can sign in, the owner included. Run this on the grid computer; it needs no sign-in. With two-factor required for staff on, admins and supervisors who are already signed in are sent to the Two-factor page until it is fixed; with the switch off, people who are already signed in are not affected:

```
reset-two-factor --all-damaged
```

That sets the damaged file aside (as `twofactor.json.damaged-...`, in the same folder). Everyone then sets two-factor up again.

## Allowed networks (admin network list)

The Security page has a list of allowed networks. Empty means any address may reach the website. Once it has entries, any other address gets an empty 404 response for the whole website: the sign-in page, the pages, the APIs and the port-8001 command routes. The setup pages are the one exception. This computer (loopback) is always allowed, so the owner cannot lock themselves out.

Do not put a reverse proxy on the same computer in front of DreamGrid. Such a proxy makes every caller look like this computer (loopback), which the list always allows, so the list gives no protection behind it. DreamGrid does not support a reverse proxy.

## Audit log

`audit.log` in the logs folder (`<data>/state/logs`) has one line of JSON for each thing staff did on the website, and for each sign-in. It records the time, who, their role, the address, the action, what it was done to, and how it ended. Staff sign-ins, refused sign-ins, role and suspension changes, two-factor changes, session endings, every change request a signed-in staff member makes, and every port-8001 command that ran and needed a level are in it. It never holds a password, a two-factor code, a backup code, a secret, the MachineId or a token. A refused sign-in names only the first name (the rest could be a password typed into the name field), except for a staff member whose password was right but whose two-factor file can't be read, or who typed a wrong two-factor code (at sign-in or on the Two-factor page): those are written with the full name. The file rolls by size like the other logs. The owner and admins read it on the Logs page: choose the "audit" source.

## Browser security headers

Every response carries a Content-Security-Policy that allows only scripts and styles from the website itself (no inline script or style), and forbids framing the site. It also sends `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin` and `X-Frame-Options: DENY`. Nothing is loaded from another origin.

## Port-8001 command API and two-factor

The patched OpenSim and Robust, and the scripts on this computer, send commands to the website on the port-8001 routes with an avatar name and password, or with the MachineId. An avatar's password is one factor only. So:

- An avatar with two-factor on must use the MachineId.
- Any admin or supervisor must use the MachineId while "two-factor required for staff" is on. The owner is not refused by the switch.
- If two-factor cannot be checked (the file is damaged), avatars are refused and only the MachineId works.
- If Robust does not answer, only the MachineId works.

The MachineId is a secret in `settings.json`. Treat it like a password and never post it.
