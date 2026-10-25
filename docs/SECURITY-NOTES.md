# Security notes

## Remote consoles

The Robust and region REST consoles listen on ports 8010 (Robust) and 8011 (region) on all network interfaces. OpenSim cannot bind them to localhost without an engine change, so each console uses a dedicated `ConsolePort` and a random 32-character `ConsolePass` generated per install and stored in `settings.json`. The launcher reads that password from there; it is never a fixed default.

Version 1 is LAN-only. The firewall and any router port forwarding must not expose ports 8010 and 8011 to the internet. Anyone who can reach those ports and knows the password can run console commands on the grid.

## Settings file

`<data>/settings.json` holds the database passwords and the console passwords, so it must be readable only by the account that runs the grid.

- On Linux the launcher writes it with mode 0600.
- On Windows it lives under `%ProgramData%\DreamGrid`. The installer (plan 3) must restrict its ACL to SYSTEM and Administrators.
