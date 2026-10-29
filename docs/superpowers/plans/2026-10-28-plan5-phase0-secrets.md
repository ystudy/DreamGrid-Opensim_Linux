# Plan 5 — Wifi work list Phase 0: secrets out of the tree, secret scan in CI

Phase 0 of `2026-10-28-wifi-replacement-worklist.md`. Small enough to do in one pass on branch `feature/phase0-secrets`.

## What was found

- Tracked live Wifi configs with real values: `bin/Wifi.ini`, `bin/config-addon-opensim/Wifi.ini`, `bin/config-addon-robust/Wifi.ini` (Gmail SMTP user and app password, the Wifi admin password, the Robust DB connection string).
- `bin/config-addon-opensim/Wifi.bak` is tracked although `*.bak` is ignored. It is a copy of the proto.
- `bin/config-addon-opensim/Wifi.ini.proto` has placeholder SMTP values but a real-looking `AdminPassword`.
- `bin/*.ini` is already ignored, so `bin/Wifi.ini` was force-added; the `config-addon-*` folders are not covered.
- `setup/package.ps1` copies `bin/` into the packages, so **every release built so far contains these files**. Revoking the values is the only fix for those; this plan only stops new copies.
- `addon-modules/21Wifi/Wifi.ini` is the upstream Diva sample with placeholder values (`your…`, the upstream default DB password). It stays.

## Rulings

| Decision | Cost if wrong |
|---|---|
| History is not rewritten (decided in the work list). The owner revokes the Gmail app password and changes the admin password. | The old values stay readable in history until revoked. |
| The live files are untracked with `git rm --cached`; working copies on disk are left alone. Other clones lose them on the next pull. | A clone that still needs Wifi copies the proto by hand (v7 runs with Wifi off). |
| The packager skips `Wifi.ini` and `Wifi.bak` by name, so a local package build can't ship a developer's live file. | One name to remove when Wifi is deleted. |
| The proto's `AdminPassword` becomes empty. Wifi is off in v7 (`[WifiService] Enabled = false` from the launcher and the smoke). | None in v7. |
| gitleaks 8.30.1, downloaded from its GitHub release and checked against its published SHA-256, not the gitleaks action (licence needed for organisations, and a pinned binary is simpler to audit). | One version bump by hand. |
| CI scans the checked-out files (`gitleaks dir`) and, for a push or PR, the new commits (`gitleaks git` over the range). Old history is not scanned, since its findings are known and accepted. | A secret that only exists in old history is not reported again. |
| The default rules miss the Gmail app password and the DB password (low entropy), so `.gitleaks.toml` adds `dreamgrid-ini-mail-or-admin-password` for `Smtp*Password` and `AdminPassword` in INI files, with placeholders ("…Password", "your…") allowed. A broader rule over every INI password flagged ~60 upstream defaults and was dropped. | A DB password committed in an INI is not caught. |
| Known non-secrets are allowed narrowly: the Launcher tests' fake token and UUIDs in the sample LSL by value (`.gitleaks.toml`); single upstream lines by fingerprint (`.gitleaksignore`): an identifier, a code comment, upstream's Gloebit sandbox key and secret, and the SMTP value in upstream's tracked `bin/Robust.HG.ini`. Rules are never turned off. | A fingerprinted line that changes is reported again (intended). |
| `RuntimeStager` skips `Wifi.ini` and `Wifi.bak` too, matching the packager, so a developer's launcher runs like a fresh checkout. | One name to remove when Wifi is deleted. |

## Tasks

1. Untrack the three live `Wifi.ini` files and `Wifi.bak`; ignore `bin/config-addon-*/Wifi.ini`.
2. Empty `AdminPassword` in `Wifi.ini.proto`.
3. Packager and `RuntimeStager` exclude `Wifi.ini` and `Wifi.bak`.
4. `.gitleaks.toml`, `.gitleaksignore` and a `secret-scan` job in `dreamgrid-ci.yml`.
5. Verify: build, unit tests, the ubODE smoke (the region no longer loads `config-addon-opensim/Wifi.ini`), and gitleaks on the tree, including that it flags the old live files when they are put back.

## Open follow-ups

- Owner: revoke the Gmail app password, change the Wifi admin password, and change the MySQL password in the live files if that database is reachable from outside.
- Upstream (Outworldz): the Gloebit sandbox key and secret, and the SMTP value in `bin/Robust.HG.ini`, are in their tree too.
