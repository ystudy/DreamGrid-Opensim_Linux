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
    var saveTimer = null, inFlight = null, unsaved = false;
    var refreshWaiting = false;  // the server's drawing came back during a drag: it is fetched once the drag ends

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
        el.style.transform = b.rotate ? "rotate(" + b.rotate + "deg)" : "";
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

    var HANDLES = ["nw", "n", "ne", "e", "se", "s", "sw", "w", "rotate"];

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
            if (name === "rotate") h.title = "Drag to turn (Shift: 15° steps)";
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

    // Every edit goes through here: it becomes an undo step, is drawn, and is saved a moment later. An edit typed in the
    // settings panel keeps the panel as it is (keepPanel), so the field that takes focus next isn't swept away.
    function change(design, keepPanel) {
        state.design = design;
        history.push(design);
        if (state.selected !== null && !blockById(state.selected)) state.selected = null;
        render();
        if (!keepPanel) renderSettingsWhenFree();
        renderPictures();
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
        renderPictures();
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

    // Saves the design as it is now. The promise says whether it is saved: true only once nothing is left unsaved. A call
    // during a save waits for it, then saves whatever changed meanwhile.
    function save() {
        clearTimeout(saveTimer);
        if (blocked) return Promise.resolve(false);
        if (inFlight) return inFlight.then(function (ok) { return ok && unsaved ? save() : ok && !unsaved; });
        if (waitingForPicture()) {
            say("Choose a picture for the new picture block.", true);
            return Promise.resolve(false);
        }
        say("Saving…");
        var sent = state.design;
        inFlight = request("PUT", api + "/draft?basedOn=" + state.version, sent).then(function (r) {
            inFlight = null;
            if (r.status === 200) {
                state.version = r.json.version;
                if (state.design === sent) unsaved = false;
                say(unsaved ? "Saving…" : "Saved");
                refreshFromServer();
                loadPictures();
                return unsaved ? save() : true;
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
            inFlight = null;
            say("Not saved: the launcher didn't answer. Your changes are still here; try Save again.", true);
            return false;
        });
        return inFlight;
    }

    // The draft as the server draws it: its stylesheet, then its blocks' HTML.
    function refreshFromServer() {
        // Replacing the blocks would take the one being dragged out from under the pointer: wait for the drag to end.
        if (drag) { refreshWaiting = true; return Promise.resolve(); }
        draftCss.href = api + "/draft.css?v=" + state.version;
        return fetch(blocksUrl, { credentials: "same-origin", headers: { Accept: "text/html" } }).then(function (r) {
            if (r.status === 401) { signInAgain(); throw new Error("signed out"); }
            return r.ok ? r.text() : null;
        }).then(function (html) {
            if (html === null) return;
            if (drag) { refreshWaiting = true; return; }
            // The launcher's own HTML for the saved draft; owner text in it is already escaped by the server.
            page.innerHTML = html;
            render();
        });
    }

    function publish() {
        var ready = unsaved || inFlight ? save() : Promise.resolve(true);
        ready.then(function (ok) {
            if (!ok) {
                if (!blocked) say("Not published: " + saveState.textContent, true);
                return;
            }
            return request("POST", api + "/publish").then(function (r) {
                if (r.status === 200) { say("Published. Viewers see it within a minute."); loadPictures(); }
                else say(r.json && r.json.problem ? r.json.problem : "Not published (HTTP " + r.status + ").", true);
            });
        }).catch(function () { say("Not published: the launcher didn't answer.", true); });
    }

    function discard(button) {
        if (!window.confirm(button.getAttribute("data-confirm"))) return;
        clearTimeout(saveTimer);
        // No save may land after the discard and bring the draft back: stop saving, and let one already sent finish first.
        blocked = true;
        (inFlight || Promise.resolve()).then(function () {
            return request("POST", api + "/discard");
        }).then(function (r) {
            if (r.status === 204) {
                unsaved = false;
                window.location.reload();
            } else {
                blocked = false;
                say("Not discarded (HTTP " + r.status + ").", true);
            }
        }, function () {
            blocked = false;
            say("Not discarded: the launcher didn't answer.", true);
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
        if (handle === "rotate") {
            // A turned block's box still has its centre where the block's centre is.
            var box = drag.el.getBoundingClientRect();
            drag.cx = box.left + box.width / 2;
            drag.cy = box.top + box.height / 2;
        }
        drag.el.setPointerCapture(e.pointerId);
        e.preventDefault();
    });

    page.addEventListener("pointermove", function (e) {
        if (!drag) return;
        var dx = (e.clientX - drag.x) / scale, dy = (e.clientY - drag.y) / scale;
        if (drag.handle === "rotate") {
            var turned = JSON.parse(JSON.stringify(drag.start));
            turned.rotate = Core.pointerAngle(drag.cx, drag.cy, e.clientX, e.clientY, e.shiftKey);
            drag.moved = turned;
            place(drag.el, turned);
            say(turned.rotate + "°");
            return;
        }
        if (drag.handle && drag.start.rotate) {
            // An edge of a turned block moves along the block's own axes, not the screen's.
            var t = -drag.start.rotate * Math.PI / 180;
            var lx = dx * Math.cos(t) - dy * Math.sin(t);
            dy = dx * Math.sin(t) + dy * Math.cos(t);
            dx = lx;
        }
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
        if (moved && (moved.x !== start.x || moved.y !== start.y || moved.w !== start.w || moved.h !== start.h ||
            (moved.rotate || 0) !== (start.rotate || 0)))
            change(replaceBlock(moved));
        if (refreshWaiting) { refreshWaiting = false; refreshFromServer(); }
    }

    // On the document, so the end of a drag is heard wherever the pointer is let go.
    document.addEventListener("pointerup", endDrag);
    document.addEventListener("pointercancel", endDrag);
    document.addEventListener("lostpointercapture", endDrag, true);

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
        return target.tagName === "INPUT" && !/^(checkbox|radio|button|submit|color|file)$/.test(target.type);
    }

    document.addEventListener("keydown", function (e) {
        if (preview || typing(e.target)) return;
        var key = e.key.toLowerCase();
        if ((e.ctrlKey || e.metaKey) && key === "z" && !e.shiftKey) { e.preventDefault(); restore(history.undo()); return; }
        if ((e.ctrlKey || e.metaKey) && (key === "y" || (key === "z" && e.shiftKey))) { e.preventDefault(); restore(history.redo()); return; }
        if (state.selected === null || e.ctrlKey || e.metaKey || e.altKey) return;
        // A key on something in the settings or pictures panel is for that control, not the block.
        if (e.target.closest && e.target.closest("#splash-settings, #splash-pictures")) return;
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

    // Every field is built with createElement and its text set as text, never as HTML, so owner text can't become markup.
    // A field commits on "change" (leaving it, or a pick), through change(…, true).

    var settings = document.getElementById("splash-settings");
    var FITS = [["Fill", "Fill (crop the edges)"], ["Fit", "Fit (show it all)"], ["Stretch", "Stretch"], ["Tile", "Tile"], ["Centre", "Centre, actual size"]];
    var ALIGNS = [["Left", "Left"], ["Centre", "Centre"], ["Right", "Right"]];
    var FAMILIES = [["Sans", "Sans-serif"], ["Serif", "Serif"], ["Monospace", "Monospace"]];
    var fieldCount = 0;

    function make(tag, className, text) {
        var el = document.createElement(tag);
        if (className) el.className = className;
        if (text !== undefined) el.textContent = text;
        return el;
    }

    function group(parent, title) {
        var g = make("div", "settings-group");
        g.appendChild(make("h3", "h6", title));
        parent.appendChild(g);
        return g;
    }

    function labelled(parent, label, input) {
        var row = make("div", "mb-2");
        input.id = "sf-" + (++fieldCount);
        var l = make("label", "form-label small mb-1", label);
        l.htmlFor = input.id;
        row.appendChild(l);
        row.appendChild(input);
        parent.appendChild(row);
        return input;
    }

    function numberField(parent, label, value, min, max, commit) {
        var input = make("input", "form-control form-control-sm");
        input.type = "number";
        input.min = String(min);
        input.max = String(max);
        input.value = String(value);
        input.addEventListener("change", function () {
            var n = Math.round(Number(input.value));
            if (!isFinite(n)) return;
            commit(Math.min(Math.max(n, min), max));
        });
        return labelled(parent, label, input);
    }

    function textField(parent, label, value, max, commit, rows) {
        var input = make(rows ? "textarea" : "input", "form-control form-control-sm");
        if (rows) input.rows = rows; else input.type = "text";
        input.maxLength = max;
        input.value = value || "";
        input.addEventListener("change", function () { commit(input.value.replace(/\r\n?/g, "\n")); });
        return labelled(parent, label, input);
    }

    function colourField(parent, label, value, commit) {
        var input = make("input", "form-control form-control-sm form-control-color");
        input.type = "color";
        input.value = value;
        input.addEventListener("change", function () { commit(input.value.toLowerCase()); });
        return labelled(parent, label, input);
    }

    function rangeField(parent, label, value, max, commit) {
        var input = make("input", "form-range");
        input.type = "range";
        input.min = "0";
        input.max = String(max);
        input.value = String(value);
        var shown = make("span", "small ms-1", value + " %");
        input.addEventListener("input", function () { shown.textContent = input.value + " %"; });
        input.addEventListener("change", function () { commit(Number(input.value)); });
        labelled(parent, label, input).parentElement.firstChild.appendChild(shown);
        return input;
    }

    function selectField(parent, label, value, options, commit) {
        var input = make("select", "form-select form-select-sm");
        options.forEach(function (o) {
            var option = make("option", null, o[1]);
            option.value = o[0];
            input.appendChild(option);
        });
        input.value = value;
        input.addEventListener("change", function () { commit(input.value); });
        return labelled(parent, label, input);
    }

    function checkField(parent, label, value, commit) {
        var row = make("div", "form-check mb-1");
        var input = make("input", "form-check-input");
        input.type = "checkbox";
        input.id = "sf-" + (++fieldCount);
        input.checked = !!value;
        input.addEventListener("change", function () { commit(input.checked); });
        var l = make("label", "form-check-label small", label);
        l.htmlFor = input.id;
        row.appendChild(input);
        row.appendChild(l);
        parent.appendChild(row);
        return input;
    }

    function button(parent, text, className, onClick) {
        var b = make("button", "btn btn-sm " + (className || "btn-outline-secondary"), text);
        b.type = "button";
        b.addEventListener("click", onClick);
        parent.appendChild(b);
        return b;
    }

    function hint(parent, text) {
        parent.appendChild(make("p", "hint form-text small mb-2", text));
    }

    // The block being edited, changed at one path, as an edit from the panel.
    function setBlock(path, value, redraw, id) {
        var b = blockById(id === undefined ? state.selected : id);
        if (b) change(replaceBlock(Core.setPath(b, path, value)), !redraw);
    }

    function setPage(path, value, redraw) {
        change(Core.setPath(state.design, path, value), !redraw);
    }

    // --- Pictures (the list itself and uploading are in the Pictures panel) ---

    state.pictures = [];

    function loadPictures() {
        return request("GET", api + "/pictures").then(function (r) {
            if (r.status === 200 && r.json) state.pictures = r.json.pictures || [];
            renderPictures();
        });
    }

    function pictureUrl(name) {
        return api + "/pictures/" + encodeURIComponent(name);
    }

    function thumb(name, label) {
        var img = make("img", "picture-thumb");
        img.src = pictureUrl(name);
        img.alt = label || "";
        img.loading = "lazy";
        return img;
    }

    // A picture choice: what is chosen now, and a list to pick from (plus "No picture" where none is allowed).
    function pictureField(parent, label, current, allowNone, commit) {
        var box = make("div", "mb-2");
        box.appendChild(make("div", "form-label small mb-1", label));
        var row = make("div", "d-flex align-items-center gap-2 mb-1");
        if (current) row.appendChild(thumb(current, "Chosen picture"));
        else row.appendChild(make("span", "small text-body-secondary", "No picture"));
        var choices = make("div", "picture-choices mt-1");
        choices.hidden = true;
        button(row, current ? "Change…" : "Choose…", null, function () { choices.hidden = !choices.hidden; });
        if (current && allowNone) button(row, "Remove", "btn-outline-danger", function () { commit(null); });
        box.appendChild(row);
        if (state.pictures.length === 0)
            choices.appendChild(make("p", "small mb-0", "No pictures yet. Upload one under Pictures."));
        state.pictures.forEach(function (p) {
            var pick = make("button", p.name === current ? "chosen" : "");
            pick.type = "button";
            pick.title = p.label;
            pick.appendChild(thumb(p.name, p.label));
            pick.addEventListener("click", function () { commit(p.name); });
            choices.appendChild(pick);
        });
        box.appendChild(choices);
        parent.appendChild(box);
    }

    // --- The page's settings ---

    function pageSettings() {
        var d = state.design, bg = d.background, lim = state.limits;
        var g = group(settings, "The page");
        hint(g, "Click a block to change it, or change the whole page here.");
        colourField(g, "Background colour", bg.colour, function (v) { setPage("background.colour", v); });
        pictureField(g, "Background picture", bg.picture && bg.picture.name, true, function (name) {
            var was = state.design.background.picture;
            setPage("background.picture", name ? { name: name, fit: (was && was.fit) || "Fill" } : null, true);
        });
        if (bg.picture) {
            selectField(g, "How the picture fills the page", bg.picture.fit, FITS, function (v) { setPage("background.picture.fit", v); });
            focusPicker(g, bg);
        }
        rangeField(g, "Darken the picture", bg.darken, lim.maxDarken || 80, function (v) { setPage("background.darken", v); });

        var size = group(settings, "Design size");
        hint(size, "The size you design at, in pixels. Viewers' windows shrink or grow it (see the preview).");
        numberField(size, "Width", d.width, lim.minWidth || 640, lim.maxWidth || 3840, function (v) {
            change(Core.fitBlocks(Core.setPath(state.design, "width", v)));
        });
        numberField(size, "Height", d.height, lim.minHeight || 400, lim.maxHeight || 2160, function (v) {
            change(Core.fitBlocks(Core.setPath(state.design, "height", v)));
        });
    }

    // A click on the picture sets the point that stays in view when it is cropped.
    function focusPicker(parent, bg) {
        var box = make("div", "mb-2");
        box.appendChild(make("div", "form-label small mb-1", "Focus point (click the part that must stay in view)"));
        var picker = make("div", "focus-picker");
        var img = thumb(bg.picture.name, "Background picture");
        img.className = "";
        var dot = make("span", "focus-dot");
        dot.style.left = bg.focusX + "%";
        dot.style.top = bg.focusY + "%";
        picker.appendChild(img);
        picker.appendChild(dot);
        picker.addEventListener("click", function (e) {
            var r = img.getBoundingClientRect();
            var x = Math.round(Math.min(Math.max((e.clientX - r.left) / r.width, 0), 1) * 100);
            var y = Math.round(Math.min(Math.max((e.clientY - r.top) / r.height, 0), 1) * 100);
            dot.style.left = x + "%";
            dot.style.top = y + "%";
            var d = Core.setPath(Core.setPath(state.design, "background.focusX", x), "background.focusY", y);
            change(d, true);
        });
        box.appendChild(picker);
        parent.appendChild(box);
    }

    // --- A block's settings ---

    function today() {
        var d = new Date();
        return d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
    }

    function blockSettings(b) {
        var lim = state.limits;
        var head = make("h2", "h6", (KIND_NAMES[b.kind] || b.kind) + " block");
        settings.appendChild(head);
        hint(settings, "Drag the block to move it, or its corners and edges to resize it. Arrow keys move it by 1 px (10 px with Shift).");

        kindSettings(b, lim);

        if (b.kind !== "Picture" || b.content.caption) {
            var t = group(settings, "Text style");
            numberField(t, "Size (design px)", b.font.size, lim.minFontSize || 8, lim.maxFontSize || 200, function (v) { setBlock("font.size", v); });
            colourField(t, "Colour", b.font.colour, function (v) { setBlock("font.colour", v); });
            checkField(t, "Bold", b.font.bold, function (v) { setBlock("font.bold", v); });
            selectField(t, "Alignment", b.font.align, ALIGNS, function (v) { setBlock("font.align", v); });
            selectField(t, "Font", b.font.family, FAMILIES, function (v) { setBlock("font.family", v); });
        }

        var look = group(settings, "Box");
        colourField(look, "Background colour", b.colour, function (v) { setBlock("colour", v); });
        rangeField(look, "See-through", b.seeThrough, lim.maxSeeThrough || 100, function (v) { setBlock("seeThrough", v); });
        pictureField(look, "Background picture", b.background && b.background.name, true, function (name) {
            var was = blockById(b.id).background;
            setBlock("background", name ? { name: name, fit: (was && was.fit) || "Fill" } : null, true);
        });
        if (b.background)
            selectField(look, "How the picture fills the box", b.background.fit, FITS, function (v) { setBlock("background.fit", v); });
        checkField(look, "Rounded corners", b.rounded, function (v) { setBlock("rounded", v); });
        numberField(look, "Padding (design px)", b.padding, 0, lim.maxPadding || 64, function (v) { setBlock("padding", v); });

        var where = group(settings, "Place, size and turn");
        [["x", "Left (design px)"], ["y", "Top (design px)"], ["w", "Width (design px)"], ["h", "Height (design px)"]].forEach(function (f) {
            var max = f[0] === "x" || f[0] === "w" ? state.design.width : state.design.height;
            numberField(where, f[1], b[f[0]], f[0] === "w" || f[0] === "h" ? Core.MIN_BLOCK : 0, max, function (v) {
                var moved = Core.inside(Core.setPath(blockById(b.id), f[0], v), state.design);
                change(replaceBlock(moved));
            });
        });
        numberField(where, "Rotation (degrees, clockwise)", b.rotate || 0, -(lim.maxRotate || 180), lim.maxRotate || 180, function (v) {
            change(replaceBlock(Core.setPath(blockById(b.id), "rotate", Core.angle(v, false))), true);
        });
        hint(where, "Or drag the round handle above the block. Hold Shift for 15° steps. 0 is upright.");
    }

    function kindSettings(b, lim) {
        var c = b.content, g;
        switch (b.kind) {
            case "Text":
                g = group(settings, "Text");
                textField(g, "Text", c.text.en, lim.maxText || 4000, function (v) { setBlock("content.text", { en: v }); }, 8);
                hint(g, "Markdown: **bold**, *italic*, [link](https://…). A blank line starts a new paragraph.");
                break;
            case "Picture":
                g = group(settings, "Picture");
                pictureField(g, "Picture", c.picture && c.picture.name, false, function (name) {
                    var was = blockById(b.id).content.picture;
                    setBlock("content.picture", { name: name, fit: (was && was.fit) || "Fill" }, true);
                });
                if (c.picture)
                    selectField(g, "How the picture fills the block", c.picture.fit, FITS, function (v) { setBlock("content.picture.fit", v); });
                textField(g, "Caption (optional)", c.caption && c.caption.en, lim.maxCaption || 200, function (v) {
                    setBlock("content.caption", v.trim() ? { en: v } : null, true);
                });
                break;
            case "GridName":
                g = group(settings, "Grid name and logo");
                checkField(g, "Show the grid's name", c.showName, function (v) { setBlock("content.showName", v); });
                checkField(g, "Show the grid's logo", c.showLogo, function (v) { setBlock("content.showLogo", v); });
                hint(g, "The logo is the grid's one logo; set it under Pictures.");
                break;
            case "Status":
                g = group(settings, "Live status");
                checkField(g, "Grid online or offline", c.status.online, function (v) { setBlock("content.status.online", v); });
                checkField(g, "People online now", c.status.people, function (v) { setBlock("content.status.people", v); });
                checkField(g, "Regions running", c.status.regions, function (v) { setBlock("content.status.regions", v); });
                hint(g, "Counts only, never names. They are at most about a minute old.");
                break;
            case "News":
                newsSettings(b, lim);
                break;
            case "Places":
                placesSettings(b, lim);
                break;
            case "Links":
                g = group(settings, "Links");
                checkField(g, "Sign up", c.links.signUp, function (v) { setBlock("content.links.signUp", v); });
                checkField(g, "Forgot password", c.links.forgotPassword, function (v) { setBlock("content.links.forgotPassword", v); });
                checkField(g, "Terms", c.links.terms, function (v) { setBlock("content.links.terms", v); });
                checkField(g, "Privacy", c.links.privacy, function (v) { setBlock("content.links.privacy", v); });
                checkField(g, "The grid's front page", c.links.frontPage, function (v) { setBlock("content.links.frontPage", v); });
                hint(g, "Sign up and forgot password show only while those pages are open; Terms and privacy only once they are written.");
                break;
        }
    }

    // Moves item i of a list one place up (-1) or down (+1) and returns the new list.
    function shifted(list, i, by) {
        var copy = list.slice(), j = i + by;
        if (j < 0 || j >= copy.length) return copy;
        var t = copy[i]; copy[i] = copy[j]; copy[j] = t;
        return copy;
    }

    // The list as it is now (the panel isn't redrawn after each typed edit, so a list kept from drawing it may be old).
    function listNow(path) {
        var at = blockById(state.selected);
        path.split(".").forEach(function (k) { at = at[k]; });
        return at;
    }

    function itemButtons(parent, count, i, path) {
        var row = make("div", "d-flex gap-1 mb-2");
        button(row, "Up", null, function () { setBlock(path, shifted(listNow(path), i, -1), true); }).disabled = i === 0;
        button(row, "Down", null, function () { setBlock(path, shifted(listNow(path), i, 1), true); }).disabled = i === count - 1;
        button(row, "Remove", "btn-outline-danger", function () {
            setBlock(path, listNow(path).filter(function (_, j) { return j !== i; }), true);
        });
        parent.appendChild(row);
        return row;
    }

    function newsSettings(b, lim) {
        var c = b.content, news = c.news;
        var g = group(settings, "News");
        textField(g, "Heading", c.heading && c.heading.en, lim.maxHeading || 120, function (v) { setBlock("content.heading", { en: v }); });
        hint(g, "Residents see the newest items first. Hidden items stay here but don't show.");
        var add = button(g, "Add news", "btn-outline-primary mb-2", function () {
            setBlock("content.news", [{ date: today(), text: { en: "" }, hidden: false }].concat(listNow("content.news")), true);
        });
        add.disabled = news.length >= (lim.maxNews || 20);
        news.forEach(function (item, i) {
            var box = make("div", "border rounded p-2 mb-2");
            var date = make("input", "form-control form-control-sm");
            date.type = "date";
            date.value = item.date;
            date.addEventListener("change", function () { if (date.value) setBlock("content.news." + i + ".date", date.value); });
            labelled(box, "Date", date);
            textField(box, "Text", item.text.en, lim.maxNewsText || 2000, function (v) { setBlock("content.news." + i + ".text", { en: v }); }, 3);
            checkField(box, "Hide this item", item.hidden, function (v) { setBlock("content.news." + i + ".hidden", v); });
            itemButtons(box, news.length, i, "content.news");
            g.appendChild(box);
        });
    }

    function placesSettings(b, lim) {
        var places = b.content.places;
        var g = group(settings, "Places to go");
        hint(g, "Up to " + (lim.maxPlaces || 8) + " regions from the whole grid. Cards show the name, a picture and a line of text.");
        var chosen = {};
        places.forEach(function (p) { chosen[p.regionId] = true; });
        var free = state.regions.filter(function (r) { return !chosen[r.id]; });
        var full = places.length >= (lim.maxPlaces || 8);
        if (state.regions.length === 0) {
            hint(g, "The region list can't be read right now.");
        } else if (!full && free.length > 0) {
            var pick = selectField(g, "Add a region", "", [["", "Choose a region…"]].concat(free.map(function (r) { return [r.id, r.name]; })), function (id) {
                var r = free.filter(function (x) { return x.id === id; })[0];
                if (!r) return;
                setBlock("content.places", listNow("content.places").concat([{ regionId: r.id, regionName: r.name, line: { en: "" }, picture: null }]), true);
            });
            pick.value = "";
        } else if (full) {
            hint(g, "This block has the most places it can hold.");
        }
        places.forEach(function (p, i) {
            var box = make("div", "border rounded p-2 mb-2");
            box.appendChild(make("div", "fw-semibold small mb-1", p.regionName));
            textField(box, "Line of text", p.line && p.line.en, lim.maxPlaceLine || 200, function (v) { setBlock("content.places." + i + ".line", { en: v }); });
            pictureField(box, "Picture", p.picture, true, function (name) { setBlock("content.places." + i + ".picture", name, true); });
            mapTileButton(box, p, i);
            itemButtons(box, places.length, i, "content.places");
            g.appendChild(box);
        });
    }

    // Copies the region's map tile into the pictures (redrawn like an upload) and uses it for place i.
    function mapTileButton(parent, place, i) {
        var blockId = state.selected;
        var b = button(parent, place.picture ? "Refresh from the map" : "Use the map tile", "btn-outline-secondary mb-2", function () {
            b.disabled = true;
            request("POST", api + "/pictures/from-region/" + encodeURIComponent(place.regionId)).then(function (r) {
                b.disabled = false;
                if (r.status !== 200 || !r.json) {
                    say(r.json && r.json.problem ? r.json.problem : "The map picture couldn't be copied (HTTP " + r.status + ").", true);
                    return;
                }
                loadPictures().then(function () {
                    // The block the button belongs to, even if another is selected by now (and if it still has place i).
                    var b = blockById(blockId);
                    if (b && b.content.places && b.content.places[i] && b.content.places[i].regionId === place.regionId)
                        setBlock("content.places." + i + ".picture", r.json.name, true, blockId);
                });
            }, function () { b.disabled = false; });
        });
    }

    // A rebuild that isn't the owner's own doing (pictures loaded, a map tile copied) waits while they type in the panel,
    // since rebuilding would throw the typed text away; it runs once they leave the field.
    function renderSettingsWhenFree() {
        var at = document.activeElement;
        if (at && settings.contains(at) && typing(at)) {
            at.addEventListener("blur", function () { setTimeout(renderSettingsWhenFree, 0); }, { once: true });
            return;
        }
        renderSettings();
    }

    // Text typed but not yet committed (the field is still focused) counts as unsaved, so leaving the page asks first.
    settings.addEventListener("input", function (e) {
        if (typing(e.target)) unsaved = true;
    });

    function renderSettings() {
        settings.textContent = "";
        if (preview) {
            hint(settings, "Go back to editing to change the page.");
            return;
        }
        if (!state.design) return;
        var b = state.selected === null ? null : blockById(state.selected);
        if (b) blockSettings(b); else pageSettings();
    }

    // --- The Pictures panel: upload, list, delete; the grid's logo -----------------------------------------------------

    var MAX_UPLOAD = 10 * 1024 * 1024;   // PictureStore.MaxUploadBytes
    var TOO_BIG = "That file is bigger than 10 MB.";
    var pictureList = document.getElementById("picture-list");
    var pictureMessage = document.getElementById("picture-message");
    var progress = document.getElementById("picture-progress");

    // Where the design being edited uses each picture, in words ("the page background", "Text block 4").
    function usesOf(name) {
        var d = state.design, found = [];
        if (!d) return found;
        if (d.background.picture && d.background.picture.name === name) found.push("the page background");
        d.blocks.forEach(function (b) {
            var label = (KIND_NAMES[b.kind] || b.kind) + " block " + b.id;
            var uses = (b.background && b.background.name === name) ||
                (b.content.picture && b.content.picture.name === name) ||
                (b.content.places || []).some(function (p) { return p.picture === name; });
            if (uses) found.push(label);
        });
        return found;
    }

    function sizeText(bytes) {
        return bytes >= 1024 * 1024 ? (bytes / 1024 / 1024).toFixed(1) + " MB" : Math.max(1, Math.round(bytes / 1024)) + " KB";
    }

    function renderPictures() {
        pictureList.textContent = "";
        if (state.pictures.length === 0) {
            pictureList.appendChild(make("li", "small text-body-secondary", "No pictures yet."));
            return;
        }
        state.pictures.forEach(function (p) {
            var li = make("li", "d-flex align-items-center gap-3 mb-2");
            li.appendChild(thumb(p.name, p.label));
            var text = make("div", "flex-grow-1 small");
            text.appendChild(make("div", "fw-semibold", p.label));
            text.appendChild(make("div", "text-body-secondary", p.width + " × " + p.height + " · " + sizeText(p.bytes)));
            var uses = usesOf(p.name);
            if (p.inUse && uses.length === 0) uses.push("the published page");
            text.appendChild(make("div", null, uses.length ? "Used by " + uses.join(", ") : "Not used"));
            li.appendChild(text);
            var del = button(li, "Delete", "btn-outline-danger", function () {
                if (!window.confirm("Delete " + p.label + "?")) return;
                request("DELETE", api + "/pictures/" + encodeURIComponent(p.name)).then(function (r) {
                    if (r.status === 204) { pictureMessage.textContent = "Deleted " + p.label + "."; loadPictures().then(renderSettingsWhenFree); }
                    else if (r.status === 409) pictureMessage.textContent = "This picture is still used on the page.";
                    else pictureMessage.textContent = "Not deleted (HTTP " + r.status + ").";
                });
            });
            del.disabled = uses.length > 0;
            if (del.disabled) del.title = "Still used on the page";
            pictureList.appendChild(li);
        });
    }

    // Sends one file as the raw body, with progress; done(json) on success, else the message to show.
    function upload(url, file, message, done) {
        if (file.size > MAX_UPLOAD) { message.textContent = TOO_BIG; return; }
        var xhr = new XMLHttpRequest();
        xhr.open("POST", url + "?label=" + encodeURIComponent(file.name));
        xhr.setRequestHeader("RequestVerificationToken", token);
        xhr.setRequestHeader("Accept", "application/json");
        var bar = progress.firstElementChild;
        progress.hidden = false;
        bar.style.width = "0%";
        xhr.upload.addEventListener("progress", function (e) {
            if (e.lengthComputable) bar.style.width = Math.round(e.loaded * 100 / e.total) + "%";
        });
        xhr.addEventListener("load", function () {
            progress.hidden = true;
            var json = null;
            try { json = JSON.parse(xhr.responseText); } catch (e) { json = null; }
            if (xhr.status === 401) signInAgain();
            else if (xhr.status === 200 && json) done(json);
            else if (xhr.status === 413) message.textContent = TOO_BIG;
            else message.textContent = json && json.problem ? json.problem : "Not uploaded (HTTP " + xhr.status + ").";
        });
        xhr.addEventListener("error", function () {
            progress.hidden = true;
            message.textContent = "Not uploaded: the launcher didn't answer.";
        });
        message.textContent = "Uploading " + file.name + "…";
        xhr.send(file);
    }

    document.getElementById("picture-file").addEventListener("change", function (e) {
        var input = e.target, file = input.files && input.files[0];
        if (!file) return;
        upload(api + "/pictures", file, pictureMessage, function (p) {
            input.value = "";
            pictureMessage.textContent = "Added " + p.label + ". Choose it in the page's or a block's settings.";
            loadPictures().then(renderSettingsWhenFree);
        });
    });

    // The logo is drawn by the server in this panel and in grid-name blocks, so the page is loaded again after a change
    // (any edit not yet saved is saved first).
    function reloadSaved() {
        (unsaved || inFlight ? save() : Promise.resolve(true)).then(function (ok) {
            if (!ok) {
                logoMessage.textContent = "Logo changed. Your page edits aren't saved yet (" + saveState.textContent +
                    "); save them, then reload the page to see the logo.";
                return;
            }
            window.location.reload();
        });
    }

    var logoMessage = document.getElementById("logo-message");
    document.getElementById("logo-file").addEventListener("change", function (e) {
        var file = e.target.files && e.target.files[0];
        if (file) upload("/api/theme/logo", file, logoMessage, reloadSaved);
    });
    var logoRemove = document.getElementById("logo-remove");
    if (logoRemove) logoRemove.addEventListener("click", function () {
        if (!window.confirm(logoRemove.getAttribute("data-confirm"))) return;
        request("DELETE", "/api/theme/logo").then(function (r) {
            if (r.status === 204) reloadSaved();
            else logoMessage.textContent = "Not removed (HTTP " + r.status + ").";
        });
    });

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
        loadPictures().then(renderSettingsWhenFree);
    });

    // For the settings and pictures parts below.
    window.SplashEditor = {
        state: state, change: change, select: select, replaceBlock: replaceBlock, blockById: blockById,
        request: request, say: say, refresh: refreshFromServer, renderSettings: function () { renderSettings(); }
    };
})();
