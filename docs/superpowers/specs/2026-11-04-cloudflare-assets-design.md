# Faster asset loading through Cloudflare — design notes

Date: 2026-11-04. On the work list's Later list. **(owner, 2026-11-04)** "I do have a Cloudflare account. So we will be testing this out and moving in that direction."

These are notes for the test and for the plan that follows it, not a finished design. Everything below is my call unless a line says it is the owner's. The open questions (section 8) have to be answered by the test before a plan is written.

## 1. Why

A visitor's viewer downloads every texture, mesh, sound and animation in a region the first time they go there. Today all of those bytes leave through the grid machine's own upload. On a home connection (often 10–40 Mbps up) that is the real limit, more than CPU or disk. A busy region, or several visitors arriving at once, saturates it, and then movement and chat lag for everyone.

Assets never change once stored (same UUID, same bytes), so they can be cached permanently. A CDN can serve each one from a data center near the visitor, and the grid machine sends each asset only once per edge location instead of once per visitor.

**(owner)** The constraints for this work: no expensive server, and limited bandwidth.

## 2. Goals and non-goals

**Goals**
- Viewers download textures and meshes (later sounds and animations) from Cloudflare, not from the grid machine.
- A cache miss is the only time the grid machine sends an asset.
- Works on the free Cloudflare plan, with no open router ports (Cloudflare Tunnel).
- The owner can switch it off and go back to today's behaviour with one setting.
- Takedowns still work: a taken-down asset stops being served, from the grid and from Cloudflare.

**Non-goals (for now)**
- Putting the whole grid behind Cloudflare. Logins, hypergrid and region traffic stay direct (section 4).
- Protecting assets from people allowed to see them. A viewer has to download a file to draw it, so anyone who can see it can copy it, with or without a CDN.
- Assets from other grids for hypergrid visitors (section 6.4).

## 3. How assets reach the viewer today

Checked in the code and on the Debian test VM (2026-11-04).

1. When a viewer enters a region, the region hands it **download addresses ("caps")**: `ViewerAsset`, `GetTexture`, `GetMesh`, `GetMesh2`. Each is set in the region's OpenSim.ini under `[ClientStack.LindenCaps]`. `"localhost"` means the region serves it at a random per-session URL; a URL means the viewer is sent there instead (`GetAssetsModule.RegisterCaps`).
2. DreamGrid's region template sets:
   - `Cap_GetTexture="${Const|BaseURL}:${Const|PublicPort}/CAPS/GetTexture/"`, which is Robust's public port, one fixed address for everyone.
   - `Cap_GetMesh="localhost"` and `Cap_GetAsset="localhost"` (the default), so the region serves meshes and `ViewerAsset` itself.
3. Robust's `/CAPS/GetTexture/` connector **did not load on Linux**. `bin/Robust.HG.ini.proto` named `Opensim.Capabilities.Handlers.dll:GetTextureSeverConnector`, and Linux file names are case-sensitive. On the VM that address returned 404. **Fixed in PR 35** (ystudy/GridGate-OpenSim#35).
4. Current Firestorm most likely uses `ViewerAsset` for textures and meshes whenever the region offers it. That is why the broken GetTexture address went unnoticed. *Not yet confirmed in Firestorm; see 8.1.*
5. Behind the region: the Flotsam cache (memory, then disk), then Robust's asset service, then FSAssets (a MySQL row UUID → hash, and a gzipped file named by the hash).

**Already in OpenSim that helps:**
- `GetTextureServerConnector` and `GetMeshServerConnector` run inside Robust and serve `/CAPS/GetTexture` and `/CAPS/GetMesh` at fixed URLs, so Cloudflare can cache them.
- Both read Robust's own `[AssetService]` settings through `[CapsService] AssetService`. FSAssets is built to have several instances in one process (only the first runs the spool writer), so turning them on is safe.
- Both have a `GetTextureRedirectURL` / `GetMeshRedirectURL` setting in `[CapsService]` that answers every request with a 301 to another address. That's useful later for mirrors, if viewers follow redirects (8.4).
- No Robust connector exists for **`ViewerAsset`**. The handler class (`GetAssetsHandler`) exists, but only regions use it.

## 4. The shape of it

```
viewer ──https──> assets.<domain> (Cloudflare edge, cached)
                        │  cache miss only
                        ▼
                  Cloudflare Tunnel (cloudflared on the grid machine)
                        │
                        ▼
                  Robust public port 8002: /CAPS/GetTexture, /CAPS/GetMesh (later /CAPS/ViewerAsset)
```

- **The grid hostname stays "DNS only" (grey cloud).** Cloudflare's free proxy doesn't carry UDP (region traffic) or ports such as 8002 and 9000+, so login, hypergrid and regions keep connecting directly.
- **Only `assets.<domain>` is proxied (orange cloud)**, through a tunnel to `http://localhost:8002`. A tunnel needs no open router port.
- **A WAF rule** on `assets.<domain>` blocks every path except `/CAPS/GetTexture`, `/CAPS/GetMesh` (and later `/CAPS/ViewerAsset`). Otherwise the tunnel would also expose login and the rest of Robust's public port under a second name.
- **A cache rule** on `assets.<domain>`: eligible for cache, edge TTL 1 year (override the origin), long browser TTL, **query string kept in the cache key** (requests are `?texture_id=<uuid>`).

Later (section 7, stage 5): the files themselves move to Cloudflare R2 and a Worker serves them, so a cache miss doesn't touch the grid machine either.

## 5. Changes this needs

### 5.1 OpenSim changes

| # | Change | Where | When |
|---|---|---|---|
| O1 | Fix the GetTexture connector name | `bin/Robust.HG.ini.proto` line 161 | **Done, PR 35** |
| O2 | Turn on Robust's mesh connector: `GetMeshConnector="${Const\|PublicPort}/OpenSim.Capabilities.Handlers.dll:GetMeshServerConnector"` | `bin/Robust.HG.ini.proto` `[ServiceList]` | Stage 1 (test by hand on the VM first) |
| O3 | **New:** a Robust `ViewerAsset` connector (`GetAssetsServerConnector`) that wraps `GetAssetsHandler` at `/CAPS/ViewerAsset`. It must **allow only the types a viewer needs to draw a scene**: `texture_id`, `mesh_id`, `sound_id`, `animatn_id`, and possibly `bodypart_id`, `clothing_id`, `gesture_id`, `settings_id`, `material_id` (8.2). `GetAssetsHandler` as written also serves `script_id`, `lsltext_id`, `notecard_id` and `object_id`. On a region those sit behind a secret per-session URL. On a public, cached address they would hand anyone a script or notecard by UUID. | `OpenSim/Capabilities/Handlers/GetAssets/` (new connector, plus an allowed-types option on the handler) | Stage 2, only if Firestorm needs ViewerAsset (8.1) |
| O4 | **New:** the Robust asset connectors refuse taken-down assets (a block list of UUIDs/hashes the launcher writes), answering 404 with a short cache time | the three connectors above | Stage 4 |
| O5 | Optional: send `Cache-Control: public, max-age=31536000, immutable` on successful asset answers, so the cache rule is a safety net rather than the only thing that makes caching work | `GetTextureRobustHandler`, `GetMeshHandler`, `GetAssetsHandler` | Stage 2 |
| O6 | Region settings that point the caps at the CDN: `Cap_GetTexture`, `Cap_GetMesh`, and `Cap_GetAsset` (either the CDN, or `""` so the viewer falls back to GetTexture/GetMesh; see 8.1) | region OpenSim.ini, `[ClientStack.LindenCaps]` (written by the launcher) | Stage 1 by hand, Stage 3 from the launcher |

Upstream OpenSim isn't changed for this. O3–O5 live in this tree.

### 5.2 Launcher and website changes (stage 3)

- An **Advanced** setting "Asset address (CDN)", empty by default. Empty means today's behaviour.
- When it is set, `GridConfigWriter` writes `Cap_GetTexture`, `Cap_GetMesh` and `Cap_GetAsset` from it for **the owner's own grid only**. Preset grids (OSgrid and others, `PresetKeys`) keep their own `Cap_GetTexture` as now.
- A **"Test the asset address"** button: requests a known library texture twice through the address and shows the `cf-cache-status` header (MISS, then HIT), plus a check that a blocked path (for example `/get_grid_info`) is refused.
- Cloudflare **purge** (stage 4): an API token (Zone → Cache Purge only), stored encrypted like the email passwords. The launcher purges an asset's URLs when a takedown removes it.
- The tunnel itself (`cloudflared`) is the owner's to install and run at first. Bundling or managing it is a later decision.

### 5.3 What the owner does in Cloudflare (by hand)

1. Add the grid's domain to Cloudflare and switch its nameservers.
2. Set the grid hostname's DNS record to **DNS only**.
3. Zero Trust → Networks → Tunnels: create a tunnel, install `cloudflared` on the grid machine, add public hostname `assets.<domain>` → `http://localhost:8002`.
4. Security → WAF → custom rule: on `assets.<domain>`, block when the path doesn't start with `/CAPS/GetTexture`, `/CAPS/GetMesh` or `/CAPS/ViewerAsset`.
5. Caching → Cache Rules: on `assets.<domain>`, eligible for cache, edge TTL override 1 year, browser TTL long, query string in the cache key (default).
6. Later: an API token with Cache Purge on this zone, entered into the launcher.

## 6. Things to know

### 6.1 Cost
Free plan: $0 for CDN bandwidth. The domain is about $10/year. R2 (stage 5) is $0.015/GB-month beyond 10 GB free, with free downloads; a Worker is $0–5/month at grid scale. Prices were checked 2026-11-04 from Cloudflare's docs and third-party summaries; check them before stage 5.

### 6.2 Assets are reachable by UUID
Robust's `/CAPS/GetTexture` is already unauthenticated today; Cloudflare doesn't change that, it just serves the same answers from closer. Two rules keep it acceptable:
- Only types a viewer needs to draw a scene are ever served from the public address (O3).
- Signed, expiring URLs (each viewer gets its own; a Worker checks it and strips it before the cache lookup) are a later option if creators ask for it. It needs a change in `GetAssetsModule.RegisterCaps` to hand each agent its own URL.

### 6.3 Takedowns
The takedown design (2.5b) **keeps the asset as evidence** and removes it only from inventories. With a CDN the asset would stay downloadable by UUID, from Cloudflare's cache for up to a year. So stage 4 must: (a) add the asset to the block list O4, so Robust answers 404; and (b) purge its URLs from Cloudflare. The case page should say both were done. Until stage 4 is built, the CDN should only be used on test grids.

### 6.4 Hypergrid visitors
A region's own caps also fetch a visitor's items from **their home grid's** asset server (`AssetServerURI`). Robust's connectors only look in this grid's assets. A hypergrid visitor's attachments whose textures were never copied here could come up grey. To test (8.3). If they do, keep `"localhost"` caps for hypergrid visitors (a small change in `GetAssetsModule`: send foreign agents to the region's own handler).

### 6.5 Switching it off
Clearing the setting restores `"localhost"` caps on the next region restart. Viewers that cached assets keep them (the UUIDs are the same).

## 7. Stages

0. **Done:** PR 35, GetTexture connector loads on Linux.
1. **Test by hand on the VM** (no code beyond O2):
   - Owner: Cloudflare steps 1–5.
   - Edit the Welcome region's OpenSim.ini by hand: `Cap_GetTexture` and `Cap_GetMesh` → `https://assets.<domain>/CAPS/...`, and try `Cap_GetAsset=""`.
   - Firestorm (owner): clear the cache, visit Welcome, check textures and meshes load; check `cf-cache-status` and the Robust log (each asset asked once).
   - This answers 8.1, 8.3 and 8.4.
2. **OpenSim changes** O3 and O5 if stage 1 shows Firestorm needs `ViewerAsset`.
3. **Launcher setting**, config writing and the test button (5.2).
4. **Takedown integration**: block list (O4) and purge. Required before a public grid turns this on.
5. **Later:** files in R2 with a Worker (the grid machine is out of the download path entirely), signed URLs, and the related ideas in section 9.

## 8. Open questions (answered by the stage 1 test)

1. **Which caps does current Firestorm use?** With `ViewerAsset` offered, does it ignore `GetTexture`/`GetMesh`? With `Cap_GetAsset=""`, does it fall back to them? If it falls back, stage 2 may not be needed.
2. **Which asset types does Firestorm fetch through `ViewerAsset`?** This sets O3's allowed list. If it fetches notecards or scripts that way, those must keep a per-session address.
3. **Hypergrid visitors:** do their attachments load with the caps on Cloudflare (6.4)?
4. **Do viewers follow a 301 on texture and mesh requests?** If yes, Robust's existing `GetTextureRedirectURL` / `GetMeshRedirectURL` can spread downloads across mirrors later.
5. **Byte ranges:** viewers fetch partial textures with `Range`. Confirm Cloudflare answers ranges from the cached copy (expected) and that a miss doesn't confuse the viewer.

## 9. Related ideas from the same discussion (later, not part of this)

Each would be its own design:
- **Fewer bytes:** a background job that finds oversized textures and re-encodes them smaller, and checks that JPEG2000 files are layered so distant objects need only the first part.
- **Less CPU per asset on one machine:** regions read FSAssets directly when Robust is local; the UUID → hash lookup kept in memory; new assets stored without gzip (textures and meshes don't shrink); a smaller Flotsam disk cache since it duplicates FSAssets on the same disk.
- **Order and fairness:** low-resolution requests before full ones, fair shares between visitors, a cap on simultaneous full downloads, and a rate limit below the real upload.
- **Cache warming:** load a region's scene assets into Flotsam when it starts.
- **Several low-bandwidth locations:** mirrors that hold popular assets everywhere and the rest twice, sync overnight, health-checked hostnames per mirror, Cloudflare in front of all of them.
- **Prefetching into the viewer:** a region manifest that a viewer could download ahead of a teleport. Needs a viewer change; any manifest lists only what a visitor would see anyway.
- **Stolen-content re-uploads:** FSAssets names files by hash, so an upload whose hash matches a taken-down asset can be refused; a perceptual hash would catch resized copies.
