# jOpenSim: notes for reflection

Date: 2026-11-06. Status: reference notes, not a plan. Nothing here is decided.

## What jOpenSim was

jOpenSim was a free Joomla extension (GPL v2) by FoTo50 that gave an OpenSim grid a website. It created in-world accounts from the site, linked existing avatars to Joomla accounts, managed groups (Flotsam groups), and fed in-world Profiles and Search through two OpenSim DLL modules. It is end of life: the last release was 0.3.2.0 for Joomla 3, in October 2020, and jopensim.com now shows only an "EOL, 2010 to 2025" notice with no downloads.

In our tree only the OpenSim-side pieces are left: `addon-modules/jOpenSimSearch` and `addon-modules/jOpenSimProfile`, plus `jOpensimURL` lines in the INI templates and `bin/config-include/GridCommon-GridServer-JOpensim.ini`. They are removed in Phase 3 of the work list (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`).

We keep these notes because jOpenSim shows what a grid website was expected to do. Some of its parts are ideas for GridGate.

## Where to look

- Source: [djphil/jopensim](https://github.com/djphil/jopensim), the "Full Pack v0.3.2.0", last pushed December 2021 (modules 0.4.0.4). Source folders only, no install zips, no license file in the repo. Uploaded by djphil, not by FoTo50.
- The developer's demo grid, archived: [jOpenSimWorld, January 2021](https://web.archive.org/web/20210125111456/https://www.jopensim.com/). Its sidebar and menu are built from jOpenSim's modules.
- The wiki, archived: links below. The owner decided (2026-11-06) that the archive is enough; jOpenSim isn't installed anywhere.

## The parts, and where GridGate stands

"Done" means GridGate already has something that does the same job. "Idea" means nothing yet.

### Components

| Part | What it did | GridGate |
|---|---|---|
| [com_opensim](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=com_opensim) | The main component: accounts, linking avatars, groups, and the web service behind in-world Profiles and Search. | Accounts, people and groups: done (plans 9, 11, 13, 14). Profiles use OpenSim's built-in service. In-world Search: idea (on the work list's Later list). |
| [jOpenSimPayPal](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=jOpenSimPayPal) | Buying in-world currency with PayPal, used with jOpenSim's money module. | Idea. Ties into the gambling-tools goal, where money is only credits. Real payments would need their own legal and security design. |

### Modules

| Part | What it did | GridGate |
|---|---|---|
| [mod_opensim_admin](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_admin) | A box on the admin control panel. | Done: the staff Home dashboard. |
| [mod_opensim_gridstatus](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_gridstatus) | Grid online or offline, region count, visitors in the last 30 days, residents, online now, hypergrid visitors. | Staff side: done (dashboard). For guests, a public status box is an idea, possibly a splash or front-page block. |
| [mod_opensim_friends](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_friends) | Shows a signed-in resident their in-world friends and who is online. | Idea, for the resident's account pages. Privacy: only the resident's own friends, and respect "show online status". |
| [mod_opensim_events](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_events) | Lists upcoming in-world events. | Idea. Belongs with in-world Search (events are part of it); the splash page could show them. |
| [mod_opensim_regions](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_regions) | A public list of the grid's regions. | Idea for guests (the goals list has "viewing regions map"). Residents already see their own regions. |
| [mod_opensim_balance](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_balance) | Shows a resident's in-world currency balance. | Idea, only once there is a money system. |
| [mod_opensim_gridinfos](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_gridinfos) | Facts about the grid. | Done: the guest Connect page. |
| [mod_opensim_jsonstats](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_jsonstats) | Shows OpenSim's JSON simulator stats. | Idea, for the staff side (Regions or the dashboard). Read on loopback only, never exposed publicly. |
| [mod_opensim_loginuri](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_loginuri) | Shows the login URI and the hypergrid URI. | Done: the guest Connect page. |
| [mod_opensim_panorama](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_panorama) | A 360° (equirectangular) picture of a region you can look around. | Idea, as a splash or front-page block. |
| [mod_opensim_teleport](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=mod_opensim_teleport) | Buttons that open the viewer at a region on the grid. | Idea, with the public regions list or "interesting places" on the splash page. |

### Plugins

| Part | What it did | GridGate |
|---|---|---|
| [plg_jopensim_quickicon](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=plg_jopensim_quickicon) | A quick link to jOpenSim in the Joomla admin. | Not needed: GridGate is the whole site. |
| [plg_jopensimregister](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=plg_jopensimregister) | Created the OpenSim account during Joomla sign-up. | Done: `/signup` (plans 9, 11). |
| [plg_jopensimprivacy](https://web.archive.org/web/20220125081453/http://wiki.jopensim.com/index.php?title=plg_jopensimprivacy) | Handled residents' privacy requests (GDPR, with Joomla 3.9's `com_privacy`). | Partly: staff can delete a person, and the legal pages exist. A resident asking for their data or for deletion themselves is an idea; it connects to 2.5c, history for legal requests, on the Later list. |

## Ideas worth a look, in short

1. In-world Search with events, run by GridGate itself (already the recommendation on the Later list). What V7 has today: `docs/opensim-search-notes.md`.
2. Public grid pages for guests: grid status, the regions list with teleport buttons, a panorama.
3. A resident's friends list on their account pages.
4. Self-service privacy requests: "send me my data" and "delete my account".
5. Staff view of simulator stats.
6. Money, balance and buying credits, only with the gambling-tools goal and its own design.
