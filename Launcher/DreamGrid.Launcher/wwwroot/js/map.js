// The Regions page map (spec 5.2): Robust's tiles through the launcher, a marker for every region the launcher
// knows, and placing a new region by dragging or clicking a type. Hand-written, no library; needs live.js.
//
// Positions are region coordinates: one per 256 m square, Y grows north, so screen y runs the other way.
// A Robust zoom z tile covers 2^(z-1) squares per side and is drawn 256 pixels wide, so zoom 1 is the closest.

// getJson(url) and getText(url) fetch from the launcher. A 401 (the sign-in expired) goes to the sign-in page.
function mapFetch(url, accept) {
    return fetch(url, { headers: { Accept: accept }, credentials: "same-origin" }).then(function (r) {
        if (r.status === 401) {
            signInAgain();
            throw new Error("signed out");
        }
        return r;
    });
}

function getJson(url) {
    return mapFetch(url, "application/json").then(function (r) {
        // The placement API explains a refused spot in a 400's body.
        var type = r.headers.get("Content-Type") || "";
        if (type.indexOf("json") < 0) throw new Error("HTTP " + r.status);
        return r.json();
    });
}

function getText(url) {
    return mapFetch(url, "text/html").then(function (r) {
        if (!r.ok) throw new Error("HTTP " + r.status);
        return r.text();
    });
}

function startMap(map) {
    var MIN_ZOOM = 1, MAX_ZOOM = 8, TILE_PX = 256, WORLD = 65536;
    var POLL_MS = 5000, ASK_GAP_MS = 250, MISSING_RETRY_MS = 60000, CLICK_SLOP_PX = 5;

    var tilesUrl = map.getAttribute("data-tiles");
    var regionsUrl = map.getAttribute("data-regions");
    var placementUrl = map.getAttribute("data-placement");
    var addUrl = map.getAttribute("data-add");
    var panelUrl = map.getAttribute("data-panel");
    // Moving a region (?move=<id>): its spot is chosen on the map and sent to its move page.
    var moveId = map.getAttribute("data-move");
    var moveName = map.getAttribute("data-move-name");
    var moveSize = parseInt(map.getAttribute("data-move-size"), 10) || 1;

    var placementText = document.getElementById("placement-text");
    var backButton = document.getElementById("back-to-mine");
    var zoomIn = document.getElementById("zoom-in");
    var zoomOut = document.getElementById("zoom-out");
    var panel = document.getElementById("region-panel");
    var panelBody = panel.querySelector(".region-panel-body");
    // The panel's HTML is fetched, so confirm.js never saw its forms: a form with data-confirm asks first here too.
    panelBody.addEventListener("submit", function (e) {
        var question = e.target.getAttribute && e.target.getAttribute("data-confirm");
        if (question && !window.confirm(question)) e.preventDefault();
    });
    var panelColumn = document.getElementById("panel-column");
    var mapColumn = document.getElementById("map-column");
    var typeItems = document.querySelectorAll("#region-types li[data-type]");

    // The view: the square at the map's centre and the zoom. Elements in the layer are placed relative to an
    // origin square, reset on each zoom, so pixel numbers stay small; panning only moves the layer's transform.
    var view = { x: 1000.5, y: 1000.5, zoom: 3 };
    var origin = { x: 1000, y: 1000 };
    var layer = document.createElement("div");
    layer.className = "map-layer";
    map.appendChild(layer);

    var tiles = {};        // "z/x/y" -> <img> in the layer
    var tileOk = {};       // "z/x/y" -> true once its picture loaded
    var tileMissing = {};  // "z/x/y" -> when it failed; tried again after MISSING_RETRY_MS
    var tileVersion = 0;   // bumped when the regions change, so changed tiles skip the browser's cache
    var regions = [];      // the latest /api/regions list
    var regionsJson = "";
    var markers = {};      // region id -> marker element
    var fitted = false;
    var selectedId = map.getAttribute("data-selected") || null;

    // Placing: the selected type, the square under the pointer and the placement answers so far.
    var placingType = null;
    var ghost = document.createElement("div");
    ghost.className = "map-ghost";
    ghost.hidden = true;
    layer.appendChild(ghost);
    var ghostSquare = null;
    var answers = {};      // "x,y" -> { ok, text }; cleared when the regions change
    var lastAsk = 0, askTimer = null;

    function scale() { return TILE_PX / Math.pow(2, view.zoom - 1); }  // pixels per square
    function span(zoom) { return Math.pow(2, zoom - 1); }               // squares per tile side
    function width() { return map.clientWidth; }
    function height() { return map.clientHeight; }
    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

    // The square (fractional) under a point given in pixels from the map's top-left corner.
    function toWorld(px, py) {
        var s = scale();
        return { x: view.x + (px - width() / 2) / s, y: view.y - (py - height() / 2) / s };
    }

    function pointIn(e) {
        var r = map.getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top, inside: e.clientX >= r.left && e.clientX < r.right && e.clientY >= r.top && e.clientY < r.bottom };
    }

    // Places el over the squares from (x, y) to (x + size, y + size), y being the south edge.
    function place(el, x, y, size) {
        var s = scale();
        el.style.left = ((x - origin.x) * s) + "px";
        el.style.top = (-(y + size - origin.y) * s) + "px";
        el.style.width = (size * s) + "px";
        el.style.height = (size * s) + "px";
    }

    function moveLayer() {
        var s = scale();
        var tx = width() / 2 - (view.x - origin.x) * s;
        var ty = height() / 2 + (view.y - origin.y) * s;
        layer.style.transform = "translate(" + Math.round(tx) + "px," + Math.round(ty) + "px)";
    }

    // Redraws after a zoom: a new origin, every element placed again, the old zoom's tiles dropped.
    function relayout() {
        origin = { x: Math.floor(view.x), y: Math.floor(view.y) };
        for (var key in tiles) { tiles[key].remove(); }
        tiles = {};
        for (var i = 0; i < regions.length; i++) {
            var r = regions[i];
            if (markers[r.id]) place(markers[r.id], r.x, r.y, r.size);
        }
        if (ghostSquare) place(ghost, ghostSquare.x, ghostSquare.y, ghostSize());
        update();
    }

    // After any move: the layer's position, the tiles in view, the zoom buttons.
    function update() {
        view.x = clamp(view.x, 0, WORLD);
        view.y = clamp(view.y, 0, WORLD);
        moveLayer();
        loadTiles();
        zoomIn.disabled = view.zoom <= MIN_ZOOM;
        zoomOut.disabled = view.zoom >= MAX_ZOOM;
    }

    function loadTiles() {
        var n = span(view.zoom), z = view.zoom;
        var a = toWorld(0, height()), b = toWorld(width(), 0);
        var x0 = clamp(Math.floor(a.x / n) * n, 0, WORLD - n), x1 = clamp(Math.floor(b.x / n) * n, 0, WORLD - n);
        var y0 = clamp(Math.floor(a.y / n) * n, 0, WORLD - n), y1 = clamp(Math.floor(b.y / n) * n, 0, WORLD - n);
        var wanted = {};
        for (var tx = x0; tx <= x1; tx += n) {
            for (var ty = y0; ty <= y1; ty += n) {
                var key = z + "/" + tx + "/" + ty;
                wanted[key] = true;
                if (tiles[key] || (tileMissing[key] && Date.now() - tileMissing[key] < MISSING_RETRY_MS)) continue;
                tiles[key] = makeTile(key, tx, ty, n);
            }
        }
        // Tiles well out of view go, so a long pan does not pile up pictures.
        for (var k in tiles) {
            if (!wanted[k]) { tiles[k].remove(); delete tiles[k]; }
        }
    }

    // The tile API ignores the query string; ?v= only gets past the browser's cache after a change.
    function tileSrc(key) { return tilesUrl + "/" + key + (tileVersion ? "?v=" + tileVersion : ""); }

    // After the regions change: the tiles in view under a changed region are fetched again, and tiles that had no
    // picture are tried again, since a region that came up or went may have redrawn them. Tiles out of view wait.
    function refreshTiles(changed) {
        tileVersion++;
        tileMissing = {};
        var n = span(view.zoom);
        changed.forEach(function (r) {
            for (var tx = Math.floor(r.x / n) * n; tx < r.x + r.size; tx += n) {
                for (var ty = Math.floor(r.y / n) * n; ty < r.y + r.size; ty += n) {
                    var img = tiles[view.zoom + "/" + tx + "/" + ty];
                    if (img) img.src = tileSrc(view.zoom + "/" + tx + "/" + ty);
                }
            }
        });
        loadTiles();
    }

    function makeTile(key, tx, ty, n) {
        var img = document.createElement("img");
        img.className = "map-tile";
        img.alt = "";
        img.draggable = false;
        place(img, tx, ty, n);
        // No picture leaves the water colour showing; the markers there get stripes.
        img.onload = function () { tileOk[key] = true; delete tileMissing[key]; markTiles(); };
        img.onerror = function () {
            img.remove();
            if (tiles[key] === img) delete tiles[key];
            delete tileOk[key];
            tileMissing[key] = Date.now();
            markTiles();
        };
        img.src = tileSrc(key);
        layer.insertBefore(img, layer.firstChild);
        return img;
    }

    // A region with no tile picture under its south-west square is striped.
    function markTiles() {
        var n = span(view.zoom);
        for (var i = 0; i < regions.length; i++) {
            var r = regions[i], el = markers[r.id];
            if (!el) continue;
            var key = view.zoom + "/" + Math.floor(r.x / n) * n + "/" + Math.floor(r.y / n) * n;
            el.classList.toggle("no-tile", !tileOk[key]);
        }
    }

    // Zooms to a level, keeping the square under the point (map pixels; the centre when absent) where it is.
    function zoomTo(zoom, px, py) {
        zoom = clamp(zoom, MIN_ZOOM, MAX_ZOOM);
        if (zoom === view.zoom) return;
        if (px === undefined) { px = width() / 2; py = height() / 2; }
        var w = toWorld(px, py);
        view.zoom = zoom;
        var s = scale();
        view.x = w.x - (px - width() / 2) / s;
        view.y = w.y + (py - height() / 2) / s;
        relayout();
        markTiles();
    }

    // Shows the box from (x0, y0) to (x1, y1) with a square to spare on each side, as close as fits.
    function fitBox(x0, y0, x1, y1) {
        var zoom = MAX_ZOOM;
        for (var z = MIN_ZOOM; z <= MAX_ZOOM; z++) {
            var s = TILE_PX / span(z);
            if ((x1 - x0 + 2) * s <= width() && (y1 - y0 + 2) * s <= height()) { zoom = z; break; }
        }
        view.zoom = zoom;
        view.x = (x0 + x1) / 2;
        view.y = (y0 + y1) / 2;
        relayout();
        markTiles();
    }

    // "Back to my regions": every live region (not the reserved spots of deleted ones).
    function fitMine() {
        var live = regions.filter(function (r) { return !r.reserved; });
        if (live.length === 0 || width() === 0) return false;
        var x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
        live.forEach(function (r) {
            x0 = Math.min(x0, r.x); y0 = Math.min(y0, r.y);
            x1 = Math.max(x1, r.x + r.size); y1 = Math.max(y1, r.y + r.size);
        });
        fitBox(x0, y0, x1, y1);
        return true;
    }

    function regionById(id) {
        for (var i = 0; i < regions.length; i++) { if (regions[i].id === id) return regions[i]; }
        return null;
    }

    // Markers: one per region, its name always on top, a class for its state.
    function showRegions(list) {
        // What changed since the last poll (state, place or size; added or gone), for the tiles. Not on the first.
        var before = {}, changed = [];
        regions.forEach(function (r) { before[r.id] = r; });
        list.forEach(function (r) {
            var old = before[r.id];
            if (!old || old.state !== r.state || old.x !== r.x || old.y !== r.y || old.size !== r.size) changed.push(r);
            delete before[r.id];
        });
        for (var gone in before) changed.push(before[gone]);
        var first = regionsJson === "";
        regions = list;
        var seen = {};
        list.forEach(function (r) {
            seen[r.id] = true;
            var el = markers[r.id];
            if (!el) {
                el = document.createElement("div");
                el.setAttribute("data-region", r.id);
                var name = document.createElement("span");
                name.className = "map-marker-name";
                el.appendChild(name);
                layer.appendChild(el);
                markers[r.id] = el;
            }
            el.className = "map-marker state-" + r.state + (r.reserved ? " reserved" : "") + (r.id === selectedId ? " selected" : "");
            el.title = r.name + ": " + r.stateText;
            el.firstChild.textContent = r.name;
            place(el, r.x, r.y, r.size);
        });
        for (var id in markers) {
            if (!seen[id]) { markers[id].remove(); delete markers[id]; }
        }
        backButton.hidden = !list.some(function (r) { return !r.reserved; });
        markTiles();
        if (!first && changed.length > 0) refreshTiles(changed);
    }

    function refresh() {
        return getJson(regionsUrl).then(function (json) {
            var text = JSON.stringify(json.regions);
            if (text === regionsJson) return;
            answers = {};
            showRegions(json.regions);
            regionsJson = text;
            fitFirst();
        });
    }

    // The first view: the selected region, else every live region. It waits until the map has a size.
    function fitFirst() {
        if (fitted || !regionsJson || width() === 0) return;
        fitted = true;
        var chosen = selectedId && regionById(selectedId);
        if (chosen) fitBox(chosen.x, chosen.y, chosen.x + chosen.size, chosen.y + chosen.size);
        else fitMine();
    }

    function resized() {
        fitFirst();
        update();
    }

    function every(ms, run) {
        function tick() {
            run().catch(function () { /* the launcher may be restarting; try again next time */ })
                .then(function () { setTimeout(tick, ms); });
        }
        tick();
    }

    // The region panel: a column from lg up, an offcanvas over the map on phones.
    function setRegionInUrl(id) {
        var url = new URL(window.location.href);
        if (id) url.searchParams.set("region", id); else url.searchParams.delete("region");
        history.replaceState(null, "", url.pathname + url.search + url.hash);
    }

    function markSelected() {
        for (var id in markers) { markers[id].classList.toggle("selected", id === selectedId); }
        document.querySelectorAll("#region-list tr[data-region]").forEach(function (tr) {
            tr.classList.toggle("table-active", tr.getAttribute("data-region") === selectedId);
        });
    }

    function isOverlay() { return window.getComputedStyle(panel).position === "fixed"; }

    function openPanel(id) {
        selectedId = id;
        setRegionInUrl(id);
        markSelected();
        panelColumn.hidden = false;
        mapColumn.classList.replace("col-lg-10", "col-lg-7");
        panelBody.textContent = "Loading…";
        if (isOverlay() && window.bootstrap) bootstrap.Offcanvas.getOrCreateInstance(panel).show();
        getText(panelUrl + encodeURIComponent(id) + "?partial=1").then(function (html) {
            if (selectedId === id) panelBody.innerHTML = html;
        }, function () {
            if (selectedId === id) panelBody.textContent = "Couldn't load this region.";
        });
        update();
    }

    function closePanel() {
        if (!selectedId) return;
        selectedId = null;
        setRegionInUrl(null);
        markSelected();
        if (panel.classList.contains("show") && window.bootstrap) bootstrap.Offcanvas.getOrCreateInstance(panel).hide();
        panelColumn.hidden = true;
        mapColumn.classList.replace("col-lg-7", "col-lg-10");
        panelBody.textContent = "";
        update();
    }

    // Placing a new region.
    function selectType(id) {
        if (moveId) return; // the spot is for the region being moved
        placingType = id;
        typeItems.forEach(function (li) {
            var on = li.getAttribute("data-type") === id;
            li.classList.toggle("selected", on);
            li.setAttribute("aria-pressed", on ? "true" : "false");
        });
        if (id) {
            map.classList.add("placing");
            showPlacement("Now choose a spot on the map.", true);
        } else {
            map.classList.remove("placing");
            hideGhost();
            showPlacement("", true);
        }
    }

    function showPlacement(text, ok) {
        placementText.textContent = text;
        placementText.classList.toggle("text-danger", !ok);
        ghost.classList.toggle("bad", !ok);
    }

    // The squares along the ghost's side: the moving region's size, else a single square.
    function ghostSize() { return moveId ? moveSize : 1; }

    function hideGhost() { ghost.hidden = true; ghostSquare = null; }

    function squareAt(p) {
        var w = toWorld(p.x, p.y);
        return { x: Math.floor(w.x), y: Math.floor(w.y) };
    }

    // The placement API's answer ({ ok, text }) for a square, asked once until the regions change. Every request
    // keeps ASK_GAP_MS after the one before (at most 4 a second): a click right after a hover waits its turn.
    function ask(sq) {
        var key = sq.x + "," + sq.y;
        if (!answers[key]) {
            var wait = Math.max(0, lastAsk + ASK_GAP_MS - Date.now());
            lastAsk = Date.now() + wait;
            answers[key] = new Promise(function (resolve) { setTimeout(resolve, wait); }).then(function () {
                // A moving region may overlap its own old square.
                var query = "?x=" + sq.x + "&y=" + sq.y + "&size=" + ghostSize() + (moveId ? "&ignore=" + encodeURIComponent(moveId) : "");
                return getJson(placementUrl + query);
            }).then(function (json) {
                return { ok: json.ok === true, text: json.text };
            }, function (e) {
                delete answers[key];
                throw e;
            });
        }
        return answers[key];
    }

    function sameSquare(a, b) { return a && b && a.x === b.x && a.y === b.y; }

    function showAnswer(sq, answer) {
        if (sameSquare(sq, ghostSquare)) showPlacement(answer.text, answer.ok);
    }

    // Asks about the ghost's square at most 4 times a second; only the latest square is asked about.
    function askSoon() {
        if (askTimer) return;
        askTimer = setTimeout(function () {
            askTimer = null;
            var sq = ghostSquare;
            if (!sq) return;
            ask(sq).then(function (a) { showAnswer(sq, a); }, function () { /* the next move asks again */ });
        }, Math.max(0, lastAsk + ASK_GAP_MS - Date.now()));
    }

    function moveGhost(p) {
        var sq = squareAt(p);
        ghost.hidden = false;
        if (sameSquare(sq, ghostSquare)) return;
        ghostSquare = sq;
        place(ghost, sq.x, sq.y, ghostSize());
        if (answers[sq.x + "," + sq.y]) ask(sq).then(function (a) { showAnswer(sq, a); }, function () { });
        else askSoon();
    }

    // A drop or a click on a spot: goes on when the spot is ok, else the reason stays shown.
    function chooseSpot(p) {
        moveGhost(p);
        var sq = ghostSquare;
        ask(sq).then(function (a) {
            showAnswer(sq, a);
            if (a.ok && sameSquare(sq, ghostSquare)) spotChosen(sq.x, sq.y);
        }, function () { /* a 401 is already handled; otherwise the person can click again */ });
    }

    // Where an ok spot leads.
    function spotChosen(x, y) {
        if (moveId) {
            window.location.href = panelUrl + encodeURIComponent(moveId) + "/move?x=" + x + "&y=" + y;
            return;
        }
        window.location.href = addUrl + "?type=" + encodeURIComponent(placingType) + "&x=" + x + "&y=" + y;
    }

    // Dragging the map pans it; a press that does not move is a click (a marker or a spot).
    var press = null;
    map.addEventListener("pointerdown", function (e) {
        if (press || (e.pointerType === "mouse" && e.button !== 0)) return;
        press = { id: e.pointerId, x: e.clientX, y: e.clientY, viewX: view.x, viewY: view.y, moved: false };
        map.setPointerCapture(e.pointerId);
        e.preventDefault();
    });
    map.addEventListener("pointermove", function (e) {
        if (press && press.id === e.pointerId) {
            var dx = e.clientX - press.x, dy = e.clientY - press.y;
            if (!press.moved && Math.abs(dx) + Math.abs(dy) > CLICK_SLOP_PX) {
                press.moved = true;
                map.classList.add("panning");
            }
            if (press.moved) {
                var s = scale();
                view.x = press.viewX - dx / s;
                view.y = press.viewY + dy / s;
                update();
            }
        }
        if (placingType && e.pointerType !== "touch" && !(press && press.moved)) moveGhost(pointIn(e));
    });
    map.addEventListener("pointerup", function (e) {
        if (!press || press.id !== e.pointerId) return;
        var clicked = !press.moved;
        press = null;
        map.classList.remove("panning");
        if (!clicked) return;
        if (placingType) { chooseSpot(pointIn(e)); return; }
        var hit = document.elementFromPoint(e.clientX, e.clientY);
        var marker = hit && hit.closest ? hit.closest(".map-marker") : null;
        if (marker) openPanel(marker.getAttribute("data-region"));
    });
    map.addEventListener("pointercancel", function () { press = null; map.classList.remove("panning"); });
    map.addEventListener("pointerleave", function (e) { if (e.pointerType !== "touch" && !press) ghost.hidden = true; });

    // The wheel zooms one level about the pointer; a burst from a touchpad counts once.
    var lastWheel = 0;
    map.addEventListener("wheel", function (e) {
        e.preventDefault();
        if (e.deltaY === 0 || Date.now() - lastWheel < 150) return;
        lastWheel = Date.now();
        var p = pointIn(e);
        zoomTo(view.zoom + (e.deltaY < 0 ? -1 : 1), p.x, p.y);
    }, { passive: false });

    zoomIn.addEventListener("click", function () { zoomTo(view.zoom - 1); });
    zoomOut.addEventListener("click", function () { zoomTo(view.zoom + 1); });
    backButton.addEventListener("click", fitMine);
    zoomIn.hidden = false;
    zoomOut.hidden = false;

    // Types: a click selects one (again unselects it); a drag carries it onto the map.
    typeItems.forEach(function (li) {
        var id = li.getAttribute("data-type");
        var drag = null;
        li.tabIndex = 0;
        li.setAttribute("role", "button");
        li.setAttribute("aria-pressed", "false");
        li.addEventListener("dragstart", function (e) { e.preventDefault(); });
        li.addEventListener("keydown", function (e) {
            if (e.key === "Enter" || e.key === " ") { e.preventDefault(); selectType(placingType === id ? null : id); }
        });
        li.addEventListener("pointerdown", function (e) {
            if (e.pointerType === "mouse" && e.button !== 0) return;
            drag = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false };
            li.setPointerCapture(e.pointerId);
            if (e.pointerType === "mouse") e.preventDefault();
        });
        li.addEventListener("pointermove", function (e) {
            if (!drag || drag.id !== e.pointerId) return;
            if (!drag.moved && Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y) > CLICK_SLOP_PX) {
                drag.moved = true;
                selectType(id);
            }
            if (!drag.moved) return;
            var p = pointIn(e);
            if (p.inside) moveGhost(p); else ghost.hidden = true;
        });
        li.addEventListener("pointerup", function (e) {
            if (!drag || drag.id !== e.pointerId) return;
            var moved = drag.moved;
            drag = null;
            if (!moved) { selectType(placingType === id ? null : id); return; }
            var p = pointIn(e);
            if (p.inside) chooseSpot(p);
        });
        li.addEventListener("pointercancel", function () { drag = null; });
    });

    // Escape unselects the type, then closes the panel.
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape") return;
        if (placingType && !moveId) selectType(null);
        else closePanel();
    });
    document.getElementById("close-panel").addEventListener("click", closePanel);
    // The phone header's close button (and Bootstrap's own Escape) hide the offcanvas.
    panel.addEventListener("hidden.bs.offcanvas", closePanel);

    if (window.ResizeObserver) new ResizeObserver(resized).observe(map);
    else window.addEventListener("resize", resized);

    if (moveId) {
        // Only the spot is asked for: the region types step aside and the map is ready to click.
        typeItems.forEach(function (li) { li.closest(".col-lg-2").hidden = true; });
        placingType = moveId;
        map.classList.add("placing");
        showPlacement("Choose a new spot for " + moveName + ".", true);
    }

    relayout();
    if (selectedId) openPanel(selectedId);
    every(POLL_MS, refresh);
}

document.addEventListener("DOMContentLoaded", function () {
    var map = document.getElementById("map");
    if (map) startMap(map);
});
