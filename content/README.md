Put the starter region at `content/starter-region.oar`. The launcher loads it into the first region when present; otherwise it generates an island.

## Owner plans

Setup starts by asking which kind of world you want. There are five plans:

- **Local** – Just me & friends (LAN). A private world on this computer, for you and people on your own network.
- **Grid Asset Server** – (LAN or WAN). Your world keeps its items on this computer and can skip regions, so more region computers can join it later.
- **Region Server** – Needs a Grid Asset Server. Adds land (regions) to your own grid, whose Grid Asset Server is another computer on your network. Before you start: set up the Grid Asset Server first and keep it running, run the same DreamGrid version on both computers, and have its address ready (for example 192.168.1.5, with `:port` only if its grid service doesn't use port 8002).
- **Connected Server** – Uses someone else's grid. This computer joins an existing world as one of its servers. Before you start: have an avatar on that grid, the grid's two addresses (its login address and its address for region servers) or choose OSgrid, and a public address people can reach this computer at. Signing in to this website uses that avatar, and the other grid checks its password.
- **Simple Public Server** – (WAN). A world open to visitors from the internet.

The network step asks whether people reach you by a LAN or a public address. It also has the **hypergrid switch**: turn it on to let avatars travel to and from other worlds. It is off by default, and you can change it later on the Advanced > Plan page.

## People who run the grid

Staff sign in to the website with their avatar name and password. The owner is the avatar that setup made the owner. After that, the owner can make other avatars supervisors or admins. A supervisor can look people up and suspend guests. An admin can use every page except the owner's two.

- **Staff** – The owner's page. See who is staff, and make someone a supervisor, an admin or a guest. A Region Server and a Connected Server don't have this page.
- **People** – Find an avatar by name, see its role, level and email, and suspend or unsuspend it. A Region Server and a Connected Server don't have this page.
- **Security** – The owner's page. Require two-factor sign-in for admins and supervisors, choose which networks may open the website, and reset someone's two-factor sign-in.
- **Sessions** – Under your own profile. See the browsers signed in as you, end one, or sign out everywhere.
- **Two-factor sign-in** – Under your own profile. Turn it on with an authenticator app on your phone. Keep the backup codes somewhere safe.

The Logs page has an **audit** source that shows what staff did.

The website uses plain HTTP for now, so on a public grid passwords and two-factor codes cross the network unencrypted. Details, and what to do if you lose your phone, are in `docs/SECURITY-NOTES.md`.

### Guests and region owners

Everyone who has an avatar on your grid is a guest, and guests have their own side of the website. It exists on Local, Simple Public Server and Grid Asset Server, not on a Region Server or a Connected Server.

- **Front page** – People who are not signed in as staff see it at `/`. It links to sign in and to create an account (sign-ups are not open yet), and has a small link for staff to sign in.
- **Guest sign-in** – Guests sign in at `/guest/sign-in` with their avatar name and password. Their page shows how to connect with Firestorm. Each door has its own lock for each avatar name, so wrong passwords at the guest door can't lock a name out of the staff sign-in; failures from one address count together at both doors. Suspending someone ends their guest sessions at once.
- **Legal pages** – Under Security, "Write the Terms, Privacy, Cookies and Contact pages" (the owner's page). You write these pages yourself in Markdown; DreamGrid supplies no wording. A box on the same page lists the cookies the website sets and what it stores, to help you write them. Tick that you wrote the pages and are responsible for them; sign-ups can open only when Terms, Privacy and Cookies are written and the box is ticked. Guests accept the Terms the first time they sign in, and again whenever you change them.
- **Bot protection** – Under Security, "Protect sign-up from bots" (the owner's page). A hidden field, a limit per address and a proof-of-work puzzle are on; a picture puzzle and admin approval of new accounts are off. "Try it" tests the layers that are on.
- **Region owners** – On a region's page an admin gives one guest region rights. The region moves into that guest's own estate when it runs, and moves back when the rights are taken away. The guest sees their regions under "My regions", and can restart a region only when it is stuck, at most once every 10 minutes, and only if every region it runs with is theirs.

The guest side's files are in `<data>/guests`. What they hold, how to recover a damaged one, and what to check after updating are in `docs/SECURITY-NOTES.md`.
