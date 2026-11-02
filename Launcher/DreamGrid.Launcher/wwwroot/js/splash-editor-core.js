// The splash page editor's rules (plan 12c): where a block may go and how big it may be, snapping and guide lines, layers,
// undo, and how far the page shrinks. No DOM and no fetch, so the tests run it through Jint; splash-editor.js does the rest.
//
// A design and its blocks are plain objects in /api/splash's JSON shape (camelCase, enums by name). Every function returns
// new objects and leaves its arguments as they were, so a design can sit in the undo history safely.

var SplashEditorCore = (function () {
    var MIN_BLOCK = 16;      // SplashRules.MinBlockSize
    var MAX_LAYER = 999;     // SplashRules.MaxLayer
    var READABLE_PX = 12;    // SplashCss.ReadableTextPx
    var DUPLICATE_OFFSET = 16;

    function copy(value) {
        return JSON.parse(JSON.stringify(value));
    }

    // A pointer event that gave no number moves nothing.
    function num(value) {
        return typeof value === "number" && isFinite(value) ? value : 0;
    }

    function clamp(value, low, high) {
        return Math.min(Math.max(value, low), high);
    }

    function snap(value, size, on) {
        return on && size > 0 ? Math.round(value / size) * size : Math.round(value);
    }

    // The block moved and resized back inside the page, whole pixels, at least MIN_BLOCK each side.
    function inside(block, design) {
        var b = copy(block);
        b.w = clamp(Math.round(b.w), MIN_BLOCK, design.width);
        b.h = clamp(Math.round(b.h), MIN_BLOCK, design.height);
        b.x = clamp(Math.round(b.x), 0, design.width - b.w);
        b.y = clamp(Math.round(b.y), 0, design.height - b.h);
        return b;
    }

    // Where a block's edges and centre lie on one axis.
    function lines(start, size) {
        return [start, start + size / 2, start + size];
    }

    // The nearest line-up of any of `mine` with any of `targets`, within tolerance: { shift, at } or null.
    function nearest(mine, targets, tolerance) {
        var best = null;
        for (var i = 0; i < mine.length; i++) {
            for (var j = 0; j < targets.length; j++) {
                var shift = targets[j] - mine[i];
                if (Math.abs(shift) <= tolerance && (best === null || Math.abs(shift) < Math.abs(best.shift)))
                    best = { shift: shift, at: targets[j] };
            }
        }
        return best;
    }

    // Every line other blocks and the page centre offer on one axis.
    function targets(others, design, axis, selfId) {
        var found = [axis === "x" ? design.width / 2 : design.height / 2];
        (others || []).forEach(function (o) {
            if (o.id === selfId) return;
            var l = axis === "x" ? lines(o.x, o.w) : lines(o.y, o.h);
            found.push(l[0], l[1], l[2]);
        });
        return found;
    }

    function move(block, dx, dy, design, opt) {
        opt = opt || {};
        var tolerance = opt.tolerance === undefined ? 4 : opt.tolerance;
        var b = copy(block);
        b.x = block.x + num(dx);
        b.y = block.y + num(dy);
        var guides = { x: [], y: [] };

        var gx = nearest(lines(b.x, b.w), targets(opt.others, design, "x", block.id), tolerance);
        if (gx) { b.x += gx.shift; guides.x.push(gx.at); }
        else b.x = snap(b.x, opt.snapSize, opt.snap);
        var gy = nearest(lines(b.y, b.h), targets(opt.others, design, "y", block.id), tolerance);
        if (gy) { b.y += gy.shift; guides.y.push(gy.at); }
        else b.y = snap(b.y, opt.snapSize, opt.snap);

        b = inside(b, design);
        return { block: b, guides: guides };
    }

    // One moving edge on one axis: lined up with a guide when one is near, else snapped.
    function edge(value, axis, block, design, opt, guides) {
        var tolerance = opt.tolerance === undefined ? 4 : opt.tolerance;
        var g = nearest([value], targets(opt.others, design, axis, block.id), tolerance);
        if (g) { guides[axis].push(g.at); return g.at; }
        return snap(value, opt.snapSize, opt.snap);
    }

    function resize(block, handle, dx, dy, design, opt) {
        opt = opt || {};
        dx = num(dx);
        dy = num(dy);
        var guides = { x: [], y: [] };
        var left = block.x, top = block.y, right = block.x + block.w, bottom = block.y + block.h;

        if (handle.indexOf("w") >= 0)
            left = clamp(edge(left + dx, "x", block, design, opt, guides), 0, right - MIN_BLOCK);
        if (handle.indexOf("e") >= 0)
            right = clamp(edge(right + dx, "x", block, design, opt, guides), left + MIN_BLOCK, design.width);
        if (handle.indexOf("n") >= 0)
            top = clamp(edge(top + dy, "y", block, design, opt, guides), 0, bottom - MIN_BLOCK);
        if (handle.indexOf("s") >= 0)
            bottom = clamp(edge(bottom + dy, "y", block, design, opt, guides), top + MIN_BLOCK, design.height);

        var b = copy(block);
        b.x = left; b.y = top; b.w = right - left; b.h = bottom - top;
        return { block: inside(b, design), guides: guides };
    }

    var NUDGE = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };

    function nudge(block, key, shift, design) {
        var step = NUDGE[key];
        if (!step) return copy(block);
        var by = shift ? 10 : 1;
        var b = copy(block);
        b.x += step[0] * by;
        b.y += step[1] * by;
        return inside(b, design);
    }

    function font() {
        return { size: 24, colour: "#ffffff", bold: false, align: "Left", family: "Sans" };
    }

    // A new block of each kind: its size and what it holds. A picture block has no picture until one is chosen.
    var defaults = {
        Text: { w: 400, h: 120, content: { text: { en: "Your text here" } } },
        Picture: { w: 320, h: 240, content: { picture: null } },
        GridName: { w: 400, h: 120, content: { showName: true, showLogo: false } },
        Status: { w: 400, h: 120, content: { status: { online: true, people: true, regions: true } } },
        News: { w: 400, h: 300, content: { heading: { en: "News" }, news: [] } },
        Places: { w: 640, h: 240, content: { places: [] } },
        Links: { w: 400, h: 120, content: { links: { signUp: true, forgotPassword: true, terms: true, privacy: true, frontPage: true } } }
    };

    function maxOf(design, field) {
        return design.blocks.reduce(function (m, b) { return Math.max(m, b[field]); }, 0);
    }

    function topLayer(design) {
        return Math.min(maxOf(design, "layer") + 1, MAX_LAYER);
    }

    function addBlock(design, kind, x, y) {
        var kindDefaults = defaults[kind];
        var d = copy(design);
        var block = {
            id: maxOf(design, "id") + 1, kind: kind,
            x: num(x) - kindDefaults.w / 2, y: num(y) - kindDefaults.h / 2, w: kindDefaults.w, h: kindDefaults.h,
            layer: topLayer(design), colour: "#000000", seeThrough: 50, background: null, rounded: false, padding: 12,
            font: font(), content: copy(kindDefaults.content)
        };
        d.blocks.push(inside(block, design));
        return d;
    }

    function find(design, id) {
        for (var i = 0; i < design.blocks.length; i++) if (design.blocks[i].id === id) return i;
        return -1;
    }

    function duplicate(design, id) {
        var i = find(design, id);
        if (i < 0) return copy(design);
        var d = copy(design);
        var b = copy(design.blocks[i]);
        b.id = maxOf(design, "id") + 1;
        b.layer = topLayer(design);
        b.x += DUPLICATE_OFFSET;
        b.y += DUPLICATE_OFFSET;
        d.blocks.push(inside(b, design));
        return d;
    }

    function remove(design, id) {
        var d = copy(design);
        d.blocks = d.blocks.filter(function (b) { return b.id !== id; });
        return d;
    }

    // Swaps layers with the nearest block above (up) or below (down); nothing changes when there is none.
    function swapLayer(design, id, up) {
        var d = copy(design);
        var i = find(d, id);
        if (i < 0) return d;
        var mine = d.blocks[i].layer, other = -1;
        d.blocks.forEach(function (b, j) {
            if (j === i) return;
            var beyond = up ? b.layer > mine : b.layer < mine;
            if (beyond && (other < 0 || (up ? b.layer < d.blocks[other].layer : b.layer > d.blocks[other].layer))) other = j;
        });
        if (other < 0) return d;
        d.blocks[i].layer = d.blocks[other].layer;
        d.blocks[other].layer = mine;
        return d;
    }

    function forward(design, id) { return swapLayer(design, id, true); }

    function back(design, id) { return swapLayer(design, id, false); }

    // SplashCss.HasText: everything but a picture block without a caption carries text.
    function hasText(b) {
        return b.kind !== "Picture" || (b.content && b.content.caption !== null && b.content.caption !== undefined);
    }

    // SplashCss.MinWidth: where the smallest text would be READABLE_PX, never below half the design width, never above it.
    function minWidth(design) {
        var sizes = design.blocks.filter(hasText).map(function (b) { return b.font.size; });
        var readable = sizes.length === 0 ? 0 : Math.ceil(READABLE_PX / Math.min.apply(null, sizes) * design.width);
        return clamp(readable, Math.floor(design.width / 2), design.width);
    }

    function maxWidth(design) {
        return Math.floor(design.width * 3 / 2);
    }

    // How wide the page box is in a viewer window viewerW wide (the public page's clamp()).
    function previewWidth(design, viewerW) {
        return clamp(viewerW, minWidth(design), maxWidth(design));
    }

    // A copy of obj with the one field at a dotted path changed ("font.size", "content.news.0.hidden").
    function setPath(obj, path, value) {
        var result = copy(obj), at = result, keys = path.split(".");
        for (var i = 0; i < keys.length - 1; i++) at = at[keys[i]];
        at[keys[keys.length - 1]] = value;
        return result;
    }

    // Every block moved and shrunk back inside the page, after its size changed.
    function fitBlocks(design) {
        var d = copy(design);
        d.blocks = d.blocks.map(function (b) { return inside(b, d); });
        return d;
    }

    // Undo and redo for one editing visit: push every design as it becomes current, the first one included.
    function History(limit) {
        var states = [], at = -1;
        return {
            push: function (design) {
                states = states.slice(0, at + 1);
                states.push(copy(design));
                if (states.length > limit) states.shift();
                at = states.length - 1;
            },
            undo: function () {
                if (at <= 0) return null;
                at--;
                return copy(states[at]);
            },
            redo: function () {
                if (at >= states.length - 1) return null;
                at++;
                return copy(states[at]);
            },
            canUndo: function () { return at > 0; },
            canRedo: function () { return at < states.length - 1; }
        };
    }

    return {
        snap: snap, move: move, resize: resize, nudge: nudge, addBlock: addBlock, duplicate: duplicate, remove: remove,
        forward: forward, back: back, minWidth: minWidth, maxWidth: maxWidth, previewWidth: previewWidth, History: History,
        setPath: setPath, fitBlocks: fitBlocks, inside: inside, defaults: defaults, MIN_BLOCK: MIN_BLOCK
    };
})();
