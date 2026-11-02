// The splash page editor (/splash, plan 12c). Needs splash-editor-core.js. Hand-written, no library.
//
// The server draws the page: after each save the canvas takes the draft's stylesheet (/api/splash/draft.css) and its
// blocks' HTML (/splash?handler=Blocks), so what staff see is what /welcome will show. This script keeps the design being
// edited, sets each block's place and size on its style object (allowed by style-src 'self'; style= attributes are not),
// and saves the design as the draft a second after the last change.

(function () {
    var root = document.getElementById("splash-editor");
    if (!root) return;
    var Core = SplashEditorCore;

    var SAVE_DELAY_MS = 1000, HISTORY = 100, GUIDE_TOLERANCE = 4;
    var CONFLICT = "Someone else changed this page. Reload to see their version.";

    var api = root.getAttribute("data-api");
    var blocksUrl = root.getAttribute("data-blocks");
    var token = root.querySelector("input[name=__RequestVerificationToken]").value;
    var page = document.getElementById("canvas-page");
    var canvas = page.parentElement;
    var viewport = document.getElementById("splash-viewport");
    var saveState = document.getElementById("save-state");
    var draftCss = document.getElementById("draft-css");

    var state = { design: null, version: 0, selected: null, limits: {}, regions: [], published: null };
    var history = Core.History(HISTORY);
    var scale = 1;
    var preview = null;          // { w, h } while previewing
    var blocked = false;         // a save was refused as stale: nothing more is saved until reload
    var saveTimer = null, saving = false, saveAgain = false, unsaved = false;

    // --- Talking to the launcher ----------------------------------------------------------------------------------

    function signInAgain() {
        window.location.href = "/login?ReturnUrl=" + encodeURIComponent(window.location.pathname);
    }

    // request(method, url, body) → { status, json, text }. A 401 (the sign-in expired) goes to the sign-in page.
    function request(method, url, body, contentType) {
        var headers = { Accept: "application/json", RequestVerificationToken: token };
        var init = { method: method, headers: headers, credentials: "same-origin" };
        if (body !== undefined) {
            headers["Content-Type"] = contentType || "application/json";
            init.body = contentType ? body : JSON.stringify(body);
        }
        return fetch(url, init).then(function (r) {
            if (r.status === 401) {
                signInAgain();
                throw new Error("signed out");
            }
            return r.text().then(function (text) {
                var json = null;
                try { json = text ? JSON.parse(text) : null; } catch (e) { json = null; }
                return { status: r.status, json: json, text: text };
            });
        });
    }

    function say(text, problem) {
        saveState.textContent = text;
        saveState.classList.toggle("text-danger", !!problem);
    }

    // --- Drawing ----------------------------------------------------------------------------------------------------

    function pct(part, whole) {
        return (part * 100 / whole) + "%";
    }

    function blockById(id) {
        for (var i = 0; i < state.design.blocks.length; i++) if (state.design.blocks[i].id === id) return state.design.blocks[i];
        return null;
    }

    function elementOf(id) {
        return page.querySelector('[data-block-id="' + id + '"]');
    }

    var KIND_NAMES = {};
    document.querySelectorAll("#splash-shelf .shelf-item").forEach(function (b) {
        KIND_NAMES[b.getAttribute("data-kind")] = b.textContent.trim();
    });

    // A block added since the last save has no HTML from the server yet: a labelled box stands in for it.
    function placeholder(block) {
        var el = document.createElement("div");
        el.className = "splash-block placeholder";
        el.setAttribute("data-block-id", String(block.id));
        el.textContent = KIND_NAMES[block.kind] || block.kind;
        page.appendChild(el);
        return el;
    }

    function place(el, b) {
        var d = state.design;
        el.style.left = pct(b.x, d.width);
        el.style.top = pct(b.y, d.height);
        el.style.width = pct(b.w, d.width);
        el.style.height = pct(b.h, d.height);
        el.style.zIndex = String(b.layer);
    }

    function sizePage() {
        var d = state.design;
        if (preview) {
            var w = Core.previewWidth(d, preview.w);
            var h = Math.round(w * d.height / d.width);
            var fit = Math.min(1, viewport.clientWidth / preview.w);
            page.style.width = w + "px";
            page.style.height = h + "px";
            page.style.margin = "0 auto";
            canvas.style.width = Math.max(preview.w, w) + "px";
            canvas.style.height = Math.max(preview.h, h) + "px";
            frame.style.width = preview.w + "px";
            frame.style.height = preview.h + "px";
            frame.style.transform = "scale(" + fit + ")";
            frameHolder.style.height = Math.ceil(preview.h * fit) + "px";
            return;
        }
        scale = viewport.clientWidth / d.width;
        page.style.width = (d.width * scale) + "px";
        page.style.height = (d.height * scale) + "px";
        page.style.margin = "";
        canvas.style.width = "";
        canvas.style.height = "";
    }

    function render() {
        sizePage();
        var ids = {};
        state.design.blocks.forEach(function (b) {
            ids[b.id] = true;
            place(elementOf(b.id) || placeholder(b), b);
        });
        page.querySelectorAll("[data-block-id]").forEach(function (el) {
            if (!ids[el.getAttribute("data-block-id")]) el.remove();
        });
        drawSelection();
        clearGuides();
        updateToolbar();
    }

    var HANDLES = ["nw", "n", "ne", "e", "se", "s", "sw", "w"];

    function drawSelection() {
        page.querySelectorAll(".splash-block.selected").forEach(function (el) { el.classList.remove("selected"); });
        page.querySelectorAll(".handle").forEach(function (h) { h.remove(); });
        if (state.selected === null || preview) return;
        var el = elementOf(state.selected);
        if (!el) return;
        el.classList.add("selected");
        HANDLES.forEach(function (name) {
            var h = document.createElement("span");
            h.className = "handle";
            h.setAttribute("data-h", name);
            el.appendChild(h);
        });
    }

    function clearGuides() {
        canvas.querySelectorAll(".guide-x, .guide-y").forEach(function (g) { g.remove(); });
    }

    function drawGuides(guides) {
        clearGuides();
        var d = state.design;
        guides.x.forEach(function (x) {
            var g = document.createElement("div");
            g.className = "guide-x";
            g.style.left = (x * scale) + "px";
            canvas.appendChild(g);
        });
        guides.y.forEach(function (y) {
            var g = document.createElement("div");
            g.className = "guide-y";
            g.style.top = (y * scale) + "px";
            canvas.appendChild(g);
        });
        return d;
    }

    function updateToolbar() {
        var some = state.selected !== null && !preview;
        ["forward", "back", "duplicate", "delete"].forEach(function (id) { document.getElementById(id).disabled = !some; });
        document.getElementById("undo").disabled = !history.canUndo() || !!preview;
        document.getElementById("redo").disabled = !history.canRedo() || !!preview;
        var full = state.design.blocks.length >= (state.limits.maxBlocks || 100);
        document.querySelectorAll("#splash-shelf .shelf-item").forEach(function (b) { b.disabled = full || !!preview; });
        document.getElementById("snap").checked = !!state.design.snap;
        document.getElementById("snap-size").value = String(state.design.snapSize);
    }

    // --- Changing the design ----------------------------------------------------------------------------------------

    // Every edit goes through here: it becomes an undo step, is drawn, and is saved a moment later.
    function change(design) {
        state.design = design;
        history.push(design);
        if (state.selected !== null && !blockById(state.selected)) state.selected = null;
        render();
        renderSettings();
        scheduleSave();
    }

    function replaceBlock(block) {
        var d = JSON.parse(JSON.stringify(state.design));
        for (var i = 0; i < d.blocks.length; i++) if (d.blocks[i].id === block.id) d.blocks[i] = block;
        return d;
    }

    function select(id) {
        state.selected = id;
        drawSelection();
        updateToolbar();
        renderSettings();
    }

    function restore(design) {
        if (!design) return;
        state.design = design;
        if (state.selected !== null && !blockById(state.selected)) state.selected = null;
        render();
        renderSettings();
        scheduleSave();
    }

    function opt() {
        return { snap: state.design.snap, snapSize: state.design.snapSize, others: state.design.blocks, tolerance: GUIDE_TOLERANCE };
    }

    // --- Saving, publishing, discarding -----------------------------------------------------------------------------

    function scheduleSave() {
        unsaved = true;
        if (blocked) return;
        clearTimeout(saveTimer);
        saveTimer = setTimeout(save, SAVE_DELAY_MS);
    }

    // The rules refuse a picture block without a picture, so such a design waits for one.
    function waitingForPicture() {
        return state.design.blocks.some(function (b) { return b.kind === "Picture" && !(b.content && b.content.picture); });
    }

    function save() {
        clearTimeout(saveTimer);
        if (blocked) return Promise.resolve(false);
        if (saving) { saveAgain = true; return Promise.resolve(false); }
        if (waitingForPicture()) {
            say("Choose a picture for the new picture block.", true);
            return Promise.resolve(false);
        }
        saving = true;
        say("Saving…");
        var sent = state.design;
        return request("PUT", api + "/draft?basedOn=" + state.version, sent).then(function (r) {
            saving = false;
            if (r.status === 200) {
                state.version = r.json.version;
                if (state.design === sent) unsaved = false;
                say("Saved");
                refreshFromServer();
                if (saveAgain) { saveAgain = false; return save(); }
                return true;
            }
            if (r.status === 409) {
                blocked = true;
                say(CONFLICT, true);
            } else if (r.status === 400 && r.json && r.json.problems) {
                say("Not saved: " + r.json.problems.join(" "), true);
            } else {
                say("Not saved (HTTP " + r.status + "). Your changes are still here; try Save again.", true);
            }
            return false;
        }, function () {
            saving = false;
            say("Not saved: the launcher didn't answer. Your changes are still here; try Save again.", true);
            return false;
        });
    }

    // The draft as the server draws it: its stylesheet, then its blocks' HTML.
    function refreshFromServer() {
        draftCss.href = api + "/draft.css?v=" + state.version;
        return fetch(blocksUrl, { credentials: "same-origin", headers: { Accept: "text/html" } }).then(function (r) {
            if (r.status === 401) { signInAgain(); throw new Error("signed out"); }
            return r.ok ? r.text() : null;
        }).then(function (html) {
            if (html === null) return;
            // The launcher's own HTML for the saved draft; owner text in it is already escaped by the server.
            page.innerHTML = html;
            render();
        });
    }

    function publish() {
        var ready = unsaved ? save() : Promise.resolve(true);
        ready.then(function (ok) {
            if (!ok && unsaved) return;
            return request("POST", api + "/publish").then(function (r) {
                if (r.status === 200) say("Published. Viewers see it within a minute.");
                else say(r.json && r.json.problem ? r.json.problem : "Not published (HTTP " + r.status + ").", true);
            });
        });
    }

    function discard(button) {
        if (!window.confirm(button.getAttribute("data-confirm"))) return;
        clearTimeout(saveTimer);
        request("POST", api + "/discard").then(function (r) {
            if (r.status === 204) {
                unsaved = false;
                window.location.reload();
            } else {
                say("Not discarded (HTTP " + r.status + ").", true);
            }
        });
    }

    // --- Pointer: move, resize, drop from the shelf -----------------------------------------------------------------

    var drag = null;

    page.addEventListener("pointerdown", function (e) {
        if (preview || e.button !== 0) return;
        // Taking the pointer here leaves focus where it was (the drag prevents the default), so let go of a field.
        if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur();
        var el = e.target.closest("[data-block-id]");
        if (!el) { select(null); return; }
        var id = Number(el.getAttribute("data-block-id"));
        if (state.selected !== id) select(id);
        var handle = e.target.classList.contains("handle") ? e.target.getAttribute("data-h") : null;
        drag = { id: id, handle: handle, x: e.clientX, y: e.clientY, start: blockById(id), moved: null, el: elementOf(id) };
        drag.el.setPointerCapture(e.pointerId);
        e.preventDefault();
    });

    page.addEventListener("pointermove", function (e) {
        if (!drag) return;
        var dx = (e.clientX - drag.x) / scale, dy = (e.clientY - drag.y) / scale;
        var r = drag.handle
            ? Core.resize(drag.start, drag.handle, dx, dy, state.design, opt())
            : Core.move(drag.start, dx, dy, state.design, opt());
        drag.moved = r.block;
        place(drag.el, r.block);
        drawGuides(r.guides);
    });

    function endDrag() {
        if (!drag) return;
        var moved = drag.moved, start = drag.start;
        drag = null;
        clearGuides();
        if (moved && (moved.x !== start.x || moved.y !== start.y || moved.w !== start.w || moved.h !== start.h))
            change(replaceBlock(moved));
    }

    page.addEventListener("pointerup", endDrag);
    page.addEventListener("pointercancel", endDrag);
    page.addEventListener("lostpointercapture", endDrag);

    // A click on the empty page (outside every block) clears the selection.
    canvas.addEventListener("pointerdown", function (e) {
        if (!preview && e.target === canvas) select(null);
    });

    function addAt(kind, x, y) {
        if (state.design.blocks.length >= (state.limits.maxBlocks || 100)) return;
        var d = Core.addBlock(state.design, kind, x, y);
        var added = d.blocks[d.blocks.length - 1];
        state.selected = added.id;
        change(d);
    }

    document.querySelectorAll("#splash-shelf .shelf-item").forEach(function (item) {
        item.draggable = true;
        item.addEventListener("dragstart", function (e) {
            e.dataTransfer.setData("text/plain", item.getAttribute("data-kind"));
            e.dataTransfer.effectAllowed = "copy";
        });
        item.addEventListener("click", function () {
            addAt(item.getAttribute("data-kind"), state.design.width / 2, state.design.height / 2);
        });
    });

    canvas.addEventListener("dragover", function (e) {
        if (preview) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = "copy";
        canvas.classList.add("drop-target");
    });
    canvas.addEventListener("dragleave", function () { canvas.classList.remove("drop-target"); });
    canvas.addEventListener("drop", function (e) {
        canvas.classList.remove("drop-target");
        var kind = e.dataTransfer.getData("text/plain");
        if (preview || !Core.defaults[kind]) return;
        e.preventDefault();
        var rect = page.getBoundingClientRect();
        addAt(kind, (e.clientX - rect.left) / scale, (e.clientY - rect.top) / scale);
    });

    // --- Keyboard and toolbar ---------------------------------------------------------------------------------------

    // Keys go to a field being typed in; a switch or button that merely kept focus doesn't take them.
    function typing(target) {
        if (!target) return false;
        if (target.isContentEditable || target.tagName === "TEXTAREA" || target.tagName === "SELECT") return true;
        return target.tagName === "INPUT" && !/^(checkbox|radio|button|submit|range|color|file)$/.test(target.type);
    }

    document.addEventListener("keydown", function (e) {
        if (preview || typing(e.target)) return;
        var key = e.key.toLowerCase();
        if ((e.ctrlKey || e.metaKey) && key === "z" && !e.shiftKey) { e.preventDefault(); restore(history.undo()); return; }
        if ((e.ctrlKey || e.metaKey) && (key === "y" || (key === "z" && e.shiftKey))) { e.preventDefault(); restore(history.redo()); return; }
        if (state.selected === null || e.ctrlKey || e.metaKey || e.altKey) return;
        if (e.key.indexOf("Arrow") === 0) {
            e.preventDefault();
            change(replaceBlock(Core.nudge(blockById(state.selected), e.key, e.shiftKey, state.design)));
        } else if (e.key === "Delete" || e.key === "Backspace") {
            e.preventDefault();
            change(Core.remove(state.design, state.selected));
        }
    });

    function on(id, handler) {
        document.getElementById(id).addEventListener("click", handler);
    }

    on("undo", function () { restore(history.undo()); });
    on("redo", function () { restore(history.redo()); });
    on("forward", function () { change(Core.forward(state.design, state.selected)); });
    on("back", function () { change(Core.back(state.design, state.selected)); });
    on("duplicate", function () {
        var d = Core.duplicate(state.design, state.selected);
        state.selected = d.blocks[d.blocks.length - 1].id;
        change(d);
    });
    on("delete", function () { change(Core.remove(state.design, state.selected)); });
    on("save", function () { save(); });
    on("publish", publish);
    on("discard", function (e) { discard(e.currentTarget); });

    function setDesignField(field, value) {
        var d = JSON.parse(JSON.stringify(state.design));
        d[field] = value;
        change(d);
    }

    document.getElementById("snap").addEventListener("change", function (e) { setDesignField("snap", e.target.checked); });
    document.getElementById("snap-size").addEventListener("change", function (e) { setDesignField("snapSize", Number(e.target.value)); });

    // --- Preview at a viewer window's size --------------------------------------------------------------------------

    var frame = document.createElement("div");
    frame.className = "viewer-frame";
    var frameHolder = document.createElement("div");
    frameHolder.appendChild(frame);
    var note = document.getElementById("preview-note");

    function startPreview(w, h) {
        if (!preview) {
            state.selected = null;
            viewport.appendChild(frameHolder);
            frame.appendChild(canvas);
        }
        preview = { w: w, h: h };
        document.getElementById("preview-off").disabled = false;
        note.hidden = false;
        note.textContent = "A viewer window " + w + " × " + h + ". The page shrinks to " + Core.minWidth(state.design) +
            " px wide, then scrolls; it grows up to " + Core.maxWidth(state.design) + " px.";
        render();
        renderSettings();
    }

    function stopPreview() {
        if (!preview) return;
        preview = null;
        viewport.appendChild(canvas);
        frameHolder.remove();
        frame.style.transform = "";
        document.getElementById("preview-off").disabled = true;
        note.hidden = true;
        render();
        renderSettings();
    }

    document.querySelectorAll("button.preview-size").forEach(function (b) {
        b.addEventListener("click", function () { startPreview(Number(b.getAttribute("data-w")), Number(b.getAttribute("data-h"))); });
    });
    on("preview-off", stopPreview);

    // --- Settings panel (the page's, or the selected block's) --------------------------------------------------------

    var settings = document.getElementById("splash-settings");

    function renderSettings() {
        settings.textContent = "";
        var p = document.createElement("p");
        p.className = "hint form-text";
        p.textContent = preview ? "Go back to editing to change the page." : state.selected === null
            ? "Click a block to change it."
            : "Drag the block to move it, or its corners and edges to resize it. Arrow keys move it by 1 px (10 px with Shift).";
        settings.appendChild(p);
    }

    // --- Start ------------------------------------------------------------------------------------------------------

    window.addEventListener("resize", function () { if (state.design) render(); });

    window.addEventListener("beforeunload", function (e) {
        if (unsaved && !blocked) { e.preventDefault(); e.returnValue = ""; }
    });

    request("GET", api).then(function (r) {
        if (r.status !== 200) { say("The editor couldn't load the page (HTTP " + r.status + ").", true); return; }
        var data = r.json;
        state.limits = data.limits || {};
        state.regions = data.regions || [];
        state.published = data.published;
        state.version = data.draftVersion;
        state.design = (data.draft || data.published || { design: data.defaultDesign }).design;
        history.push(state.design);
        unsaved = false;
        render();
        renderSettings();
        say(data.draft ? "Draft" : data.published ? "Published page" : "The built-in page");
    });

    // For the settings and pictures parts below.
    window.SplashEditor = {
        state: state, change: change, select: select, replaceBlock: replaceBlock, blockById: blockById,
        request: request, say: say, refresh: refreshFromServer, renderSettings: function () { renderSettings(); }
    };
})();
