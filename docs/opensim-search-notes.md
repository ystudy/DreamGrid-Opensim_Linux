# In-viewer search: what V7 has today

Date: 2026-11-06. Status: findings from reading the code and the config templates. Nothing here was tested on the VM unless it says so.

## In short

On the owner's own grid, only **People** and **Groups** search work today. Places, Popular places, Land, Events and Classifieds come back empty, because no search web service is set up. The viewer side is already built (OpenSimSearch); what's missing is the service behind it.

## The four pieces

### 1. BasicSearchModule — core OpenSim, on

- **Covers:** the People and Groups tabs of the viewer's Search window.
- **How:** it answers the viewer's "find" request itself. People come from Robust's user accounts (`UserAccountService.GetUserAccounts`, with a short cache); groups from the groups service's `FindGroups`. No web service is needed.
- **Where:** `OpenSim/Region/CoreModules/Framework/Search/BasicSearchModule.cs`. Turned on by `SearchModule = "BasicSearchModule"` in `Grid.ini` and `GridHypergrid.ini`.

### 2. DataSnapshot — core OpenSim, half on

- **Covers:** nothing visible on its own. It is the data feed an outside search engine reads.
- **How:** every `default_snapshot_period` (300 s), or after `max_changes_before_update` (10) changes, each region writes an XML snapshot of its parcels, objects and land for sale. It serves the snapshot on its HTTP port and announces itself to every address in `data_services`, so a search engine knows to come and collect it.
- **Today:** the region template (`bin/config-include/Proto/Robust/OpenSim.ini`, `[DataSnapshot]`) has `index_sims=True` and `data_exposure=all`. `data_services` points at outworldz.com, which the launcher comments out (`GridConfigWriter.ThirdParties`). So snapshots are made, and nobody is told about them.
- **Where:** `OpenSim/Region/OptionalModules/DataSnapshot/DataSnapshotManager.cs`.

### 3. OpenSimSearch — add-on, built, off on our own grid

- **Covers:** the rest of the Search window: Places, Popular places, Land (for sale, auctions, price and area), Events (list and details), Classifieds (list and details), and the land-for-sale and event markers on the world map.
- **How:** a messenger only. Each viewer query becomes an XML-RPC call to the web service at `[Search] SearchURL`: `dir_places_query`, `dir_popular_query`, `dir_land_query`, `dir_events_query`, `dir_classified_query`, `event_info_query`, `classifieds_info_query`. That service keeps the database, which it fills by collecting the DataSnapshot feeds.
- **Turns on** only when `[Search] Module` is `OpenSimSearch` (the default) and `SearchURL` isn't empty.
- **Today, own grid:** the template's `SearchURL=https://outworldz.com/Search/query.php` is commented out by the launcher, so `SearchURL` is empty and the module stays off.
- **Today, connected to OSGrid:** the launcher copies the preset's whole `[Search]` section (`GridConfigWriter.PresetSections`), `Module="OpenSimSearch"` and `SearchURL=http://search.osgrid.org/v2/query.php`, so it's on and uses OSGrid's search.
- **Where:** `addon-modules/OpenSimSearch/Modules/SearchModule/OpenSearch.cs`, about 700 lines. Upstream: `kcozens/OpenSimSearch`.

### 4. jOpenSimSearch — add-on, built, off on our own grid

- **Covers:** the same Search tabs as OpenSimSearch. It also pushes data itself: it tells the jOpenSim website when a region goes online or offline, asks it to refresh the region's data (`init_SearchDataUpdate`, `searchDataUpdate`), watches avatars entering parcels, and adds a `search update` console command.
- **How:** the same XML-RPC calls, aimed at jOpenSim's PHP pages (`com_opensim`), which no longer exist (see `docs/jopensim-notes.md`).
- **Turns on** when `SearchURL` is set, `[DataSnapshot] index_sims` is true and `default_snapshot_period` has a value. It never looks at `[Search] Module`.
- **Where:** `addon-modules/jOpenSimSearch/Modules/jOpenSimSearch/jOpenSimSearch.cs`, about 1,000 lines. Removed in Phase 3 of the work list.

### Not a search module: jOpenSimProfile

`addon-modules/jOpenSimProfile` is a single `.cs` file with no project; it isn't built or loaded. Profiles, picks and classifieds are stored by OpenSim's own `UserProfilesService` through Robust (`[UserProfiles] ProfileServiceURL`). Classifieds only show up in Search if a search service collects them.

## A likely problem on OSGrid (not tested)

Because jOpenSimSearch ignores `[Search] Module`, a region connected to OSGrid probably loads **both** search modules: OSGrid's template sets `SearchURL`, `index_sims=true` and a snapshot period. Both would answer the same viewer queries, and jOpenSimSearch would send its jOpenSim-only calls to `search.osgrid.org`, which would answer with errors. To check on the VM: connect a region to OSGrid and look for `[jOpenSimSearch] Search module is activated` next to `[SEARCH] OpenSimSearch module is active` in its log. Deleting jOpenSimSearch in Phase 3 removes the problem either way.

## What a full search needs

- Keep OpenSimSearch (viewer side) and BasicSearchModule (people and groups).
- Provide a search web service that collects the regions' DataSnapshot feeds, stores places, parcels, land for sale, events and classifieds, and answers the `dir_*` and `*_info_query` calls.
- Point `[Search] SearchURL` and `[DataSnapshot] data_services` at it.
- Optionally `[LoginService] SearchURL` in Robust, for the viewer's web search tab.
- Posting ads and events from GridGate writes into the same database.

The work list's Later section already recommends that GridGate provide this service itself, with no PHP, the same on Windows and Debian. It also notes the safety rules: public data only, rate limited, and only known simulators may register.
