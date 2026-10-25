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
