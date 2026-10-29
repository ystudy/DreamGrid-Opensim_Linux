Put the starter region at `content/starter-region.oar`. The launcher loads it into the first region when present; otherwise it generates an island.

## Owner plans

Setup starts by asking which kind of world you want. There are five plans:

- **Local** – Just me & friends (LAN). A private world on this computer, for you and people on your own network.
- **Grid Asset Server** – (LAN or WAN). Your world keeps its items on this computer and can skip regions, so more region computers can join it later.
- **Region Server** – Needs a Grid Asset Server. Adds land (regions) to your own grid, whose Grid Asset Server is another computer on your network. Before you start: set up the Grid Asset Server first and keep it running, run the same DreamGrid version on both computers, and have its address ready (for example 192.168.1.5, with `:port` only if its grid service doesn't use port 8002).
- **Connected Server** – Uses someone else's grid. This computer joins an existing world as one of its servers.
- **Simple Public Server** – (WAN). A world open to visitors from the internet.

Connected Server arrives in a later update; its card shows a disabled Choose button for now.

The network step asks whether people reach you by a LAN or a public address. It also has the **hypergrid switch**: turn it on to let avatars travel to and from other worlds. It is off by default, and you can change it later on the Advanced > Plan page.
