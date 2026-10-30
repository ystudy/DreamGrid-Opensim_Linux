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
