# HTTPS for the launcher website — design

> **Shelved 2026-10-29 (owner): kept as future build notes, not approved for building.** HTTPS moves to the future public WAN website ("advanced grid"). Install and owner plans come first: `2026-10-29-install-and-plans-design.md`.

Phase 1.1 of `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`, narrowed in conversation with the owner on 2026-10-29. Marked **(owner)** where the owner decided, **(my call)** where the designer did; everything else is left to the builder.

## 1. Why

The launcher website will take over what Wifi did: sign-up, sign-in and password pages that guests reach over the internet as well as the LAN. Today the site is plain HTTP on every interface at the web port (default 8001; `Program.cs`, `Listen(IPAddress.Any, settings.WebPort)`), so passwords and the admin sign-in cookie cross the network in the clear. Every later Phase 1 and Phase 2 item (Secure cookies, TOTP, the SMTP password field, sign-up) assumes HTTPS can be on.

## 2. The bigger picture (context only, not built here)

**(owner)** The long-term vision: several computers on a LAN, one public-facing website for admins and guests (administration, monitoring, sign-up, forum, status), and an API on each local machine so that website can see what is happening on it. That **global version** is a later project.

This design is the **local version**: one machine serves its own site at `https://<grid address>:8001`. First use runs setup; afterwards the same site serves residents and the admin. Guests come in over the internet, the LAN, or both, as the owner needs. **(owner)** No reverse proxy is required.

## 3. Goals

- An owner can turn HTTPS on and off from **Advanced settings** once the grid runs. **(owner)** It is **off by default** and is **not** offered in first-use setup.
- Certificate: either one the launcher makes itself (self-signed) or one the owner supplies (PEM or PFX).
- While HTTPS is on: plain-HTTP requests from browsers are redirected to HTTPS; the sign-in and antiforgery cookies are `Secure`.
- Nothing the owner does in these settings can lock them out of their own site.

## 4. Non-goals (now) — kept in §9, future build notes

Let's Encrypt; the outside reachability tester; router port forwarding and firewall help in setup; domain names and dynamic DNS; reverse-proxy support and documentation; the multi-machine global version.

## 5. Anti-goals

- An owner locked out of their site (HSTS pinned to a certificate the browser rejects, a bad upload, a lost key).
- Breaking the calls patched OpenSim makes to the launcher, or the welcome page Firestorm shows at login.
- A private key that crosses the network in the clear, or that anyone but the launcher's account can read.

## 6. Constraints

- **Bare IP addresses are the usual case (owner).** Public certificate authorities don't help there, so most owners who turn HTTPS on use the self-signed certificate. **Every guest then sees the browser's "connection is not private" warning** on their first visit and must click through it. The Advanced page says so plainly before the owner turns it on. This is why HTTPS is off by default.
- **One port.** The site keeps its single web port; HTTPS and plain HTTP share it (§7.3).
- **Patched OpenSim calls back over plain HTTP to loopback:** `RegionReady alert_uri` = `http://127.0.0.1:<WebPort>/dreamgrid/regionready`, `SmartStart URL` = `http://127.0.0.1:<WebPort>` (`GridConfigWriter`). OpenSim cannot accept a self-signed certificate.
- **Firestorm's login screen loads `[GridInfoService] welcome`** = `http://<host>:<WebPort>/welcome` in its built-in browser, which does not let the user accept a self-signed certificate.
- Windows (service as `NT SERVICE\DreamGrid`, data under `%ProgramData%\DreamGrid`) and Debian 12/13 (systemd, data dir `0700`). Everything works the same on both.
- The site needs no internet access (offline LANs); nothing in this design calls out.
- All settings changes go through `IGridController.UpdateSettings` (plan 2a ruling).

## 7. How it works

### 7.1 Advanced settings → HTTPS (my call: its own section on the Network page, or its own page; builder's choice)

- Shows the current state: off, or on with which certificate, its names, and its expiry date.
- **Turn on with a self-signed certificate.** Before it acts, it explains the browser warning guests will see.
- **Use my own certificate:** upload a PEM pair (certificate chain + key, key unencrypted or with a password) or a PFX with its password. The launcher checks that the key matches, the chain parses, and the certificate is valid now. It warns, but does not refuse, when no name in the certificate matches the grid's hostname (§7.2) or the certificate expires within 30 days.
- **(my call) Uploading a key is only accepted over HTTPS or from the machine itself** (a loopback connection). Otherwise the page says: turn on the self-signed certificate first, then upload yours over the secure connection. This keeps the private key off the network in the clear.
- **Turn off:** back to plain HTTP. The certificate files are kept so turning it on again doesn't need a new upload.
- **No HSTS (my call).** HSTS is keyed by host, not port, so it would force HTTPS onto Robust's public port, region HTTP ports (`llRequestURL`, shared media) and any later plain-HTTP module for every guest who received it, and that can't be undone in their browsers until it expires. Browsers ignore it for bare IP addresses anyway (RFC 6797), the usual case here; and with a self-signed certificate it removes the "proceed anyway" button. Moved to §9.
- **(my call) Changes apply without restarting the launcher.** The page then reloads at the new address (`https://…` or `http://…`, same host and port). If that proves unsafe in Kestrel, the builder may fall back to "applies when the launcher restarts" with the same notice the web-port change uses (`Network.cshtml.cs`, `Notes`).

### 7.2 The self-signed certificate (my call)

- Made by the launcher, no external tools. Its names: the grid hostname setting (`Hostname`, the address shown to viewers, which may be the public IP), the detected LAN addresses, the machine name, `localhost`, `127.0.0.1` and `::1`.
- Re-made automatically when those names change (for example the hostname setting changes) or 30 days before it expires. Guests then see the warning again; the Advanced page says when this happened.
- Key type, size and lifetime are the builder's choice (a modern default such as ECDSA P-256 or RSA 2048; a lifetime long enough that guests aren't re-warned often).

### 7.3 One port, both protocols

- The listener on the web port accepts both TLS and plain HTTP connections (the builder detects which from the first bytes of each connection, or uses an equivalent Kestrel mechanism).
- **While HTTPS is off:** exactly today's behaviour.
- **While HTTPS is on, a plain-HTTP request is:**
  - **served as plain HTTP** if it is a port-8001 contract call from loopback: `POST /dreamgrid/regionready`, the SmartStart calls, and the legacy command API on `/` (`LegacyEndpoints`, `LegacyRootPolicy`). Loopback means `LegacyEndpoints.IsLoopbackCaller`. **(my call)** From any other address these get no plain-HTTP service (they must use HTTPS).
  - **served as plain HTTP** if it is the viewer welcome page (`/welcome` and only the static files it uses), from anywhere. **(my call)** It has no secrets and no forms, and Firestorm can't show it otherwise. It must set no cookies when served over HTTP.
  - **served as plain HTTP** if it is `GET /api/status` from anywhere. **(my call)** It is already anonymous and always open (`SetupGate.AlwaysOpen`), and outside status monitors usually can't accept a self-signed certificate. An admin's session cookie is never sent over plain HTTP (`Secure`), so it answers with the anonymous view.
  - **otherwise redirected** (308 for non-GET so a form post isn't silently turned into a GET; 301 or 308 for GET) to `https://<same host>:<same port><same path and query>`.
- Over HTTPS everything works as today.

### 7.4 Cookies and headers

- While HTTPS is on, the `dg-auth` sign-in cookie and the antiforgery cookie are `Secure` (always, not "same as request"). Turning HTTPS on signs nobody out unnecessarily; the builder decides whether existing sessions need a fresh sign-in.
- No HSTS (§7.1).

### 7.5 Storage

- Certificate and key live under the owner-only data directory (`LauncherPaths.CreateOwnerOnly`; Windows ACL from plan 3). The PFX password, if any, is never written in plain text (data protection, as the cookie keys use) and never shown back on a page.
- Settings record: HTTPS on/off, which certificate (self-signed or owner-supplied). Schema version bumped as settings changes require.
- Uploads and turning on or off go to `launcher.log` (who, what, when; never the key or password). The Phase 1.3 audit log takes these over later.

### 7.6 Getting back in

- **(my call)** A launcher verb, `https off --data <dir>`, in the style of `set-web-port` (instance lock, exit 3 while a launcher runs), turns HTTPS off for an owner who can't reach the site. The Advanced page and the README mention it.
- If the stored certificate can't be loaded at startup (deleted, corrupt, wrong password), the launcher starts on plain HTTP, logs why in plain words, and the dashboard shows the problem to the admin.

## 8. Testing

- Unit: self-signed generation and its names; PEM and PFX import, mismatched key, expired, name mismatch warning; the redirect and exemption rules (contract calls from loopback vs elsewhere, `/welcome`, `/api/status`, everything else); the off-by-default setting and schema change.
- Integration (both OSes, as the existing launcher end-to-end tests run): turn HTTPS on through the Advanced page, then sign in over HTTPS; a plain-HTTP page request redirects; a region still reaches LOGINS ENABLED (its RegionReady alert still arrives); `/welcome` still answers over HTTP; turn it off again; `https off` verb; a corrupt certificate file starts on HTTP with the notice.

## 9. Future build notes (owner: keep for later)

- **Let's Encrypt**, as an advanced option for the global version on ports 80/443 (HTTP-01 needs port 80, TLS-ALPN-01 needs 443; neither works on 8001). On Debian, binding 80/443 needs `CAP_NET_BIND_SERVICE` in the systemd unit; on Windows, a firewall rule from the installer. Let's Encrypt also issues short-lived certificates for bare IP addresses, which may suit owners without a domain.
- **HSTS**, only for a DNS name with a trusted certificate, and only once every plain-HTTP port on that host (Robust, regions) is covered or the owner accepts the break.
- **Domain names**, including dynamic DNS such as the Outworldz names. The owner wants the option.
- **The outside tester** that confirms the site works from the internet. From inside the LAN many routers don't loop a request back to the public address, so a real test likely needs an outside service (perhaps Outworldz) to call in.
- **Router and firewall help in first-use setup** (UPnP port mapping with step-by-step fallback; the Debian firewall). Belongs with the tester: without it the launcher can't tell whether a port forward worked.
- **Reverse proxy** (Caddy or nginx) documentation, with forwarded headers trusted only from a configured proxy address.
- **The global version:** one public site for several machines, with an API on each local machine.
