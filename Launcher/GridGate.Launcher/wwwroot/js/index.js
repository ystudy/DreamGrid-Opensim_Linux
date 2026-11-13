// The dashboard: copy button, grid controls through /api/grid/*, the live status (every 5 seconds) and the numbers (every 30).
(function () {
    copyButton(document.getElementById("login-uri"), document.getElementById("copy-uri"));

    var form = document.getElementById("controls");
    var notice = document.getElementById("notice");
    var token = form.querySelector("input[name=__RequestVerificationToken]").value;
    // With JavaScript the controls call /api/grid/* and the page stays put; without it the form posts.
    form.addEventListener("submit", function (e) {
        var button = e.submitter;
        if (!button || !window.fetch) return;
        e.preventDefault();
        fetch("/api/grid/" + encodeURIComponent(button.value), {
            method: "POST",
            credentials: "same-origin",
            headers: { RequestVerificationToken: token, Accept: "application/json" }
        })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r); })
            .then(function (j) { notice.textContent = j.notice; })
            .catch(function () { notice.textContent = "That did not work. Reload the page and try again."; });
    });

    function el(tag, className, text, href) {
        var e = document.createElement(href ? "a" : tag);
        if (href) e.href = href;
        if (className) e.className = className;
        if (text) e.textContent = text;
        return e;
    }

    function badge(state, text, push) {
        return el("span", "badge state-" + state.toLowerCase() + (push ? " ms-auto" : ""), text);
    }

    function render(s) {
        // The page was rendered for an admin whose sign-in has since expired.
        if (s.admin === false) { signInAgain(); return; }
        var headline = document.getElementById("headline");
        headline.textContent = s.headline;
        headline.className = "headline" + (s.problem ? " problem" : "");

        var list = document.getElementById("components");
        list.textContent = "";
        [s.grid.database, s.grid.robust].forEach(function (c) {
            var li = document.createElement("li");
            var left = el("span");
            left.appendChild(el("span", "component-label", c.label));
            left.appendChild(document.createTextNode(" "));
            left.appendChild(el("span", "row-sub", c.detail));
            li.appendChild(left);
            li.appendChild(badge(c.state, c.stateText, true));
            list.appendChild(li);
        });

        var regions = document.getElementById("region-rows");
        regions.textContent = "";
        var running = 0, parked = 0;
        s.grid.regions.forEach(function (r) {
            if (r.state === "Running") running++;
            if (r.state === "Parked") parked++;
            var li = document.createElement("li");
            var left = el("span");
            left.appendChild(el("span", "component-label", r.name, r.id ? "/regions?region=" + encodeURIComponent(r.id) : null));
            if (r.runsWith && r.runsWith.length) {
                left.appendChild(document.createTextNode(" "));
                left.appendChild(el("span", "row-sub", "runs with " + r.runsWith.join(", ")));
            }
            li.appendChild(left);
            var counted = typeof r.people === "number";
            if (counted) li.appendChild(el("span", "ms-auto row-sub", r.people + " here"));
            li.appendChild(badge(r.state, r.stateText, !counted));
            regions.appendChild(li);
        });
        document.getElementById("tile-regions").textContent = running + " of " + s.grid.regions.length;
        document.getElementById("tile-regions-sub").textContent = "running · " + parked + " parked";
        var onlineTile = document.getElementById("tile-online");
        if (onlineTile) onlineTile.textContent = String(s.online.length);

        var online = document.getElementById("online");
        var note = document.getElementById("online-note");
        var links = online.getAttribute("data-links") === "people";
        online.textContent = "";
        (s.onlinePeople || []).forEach(function (p) {
            var li = document.createElement("li");
            var known = links && p.id && p.id !== "00000000-0000-0000-0000-000000000000";
            li.appendChild(el("span", "component-label", p.name, known ? "/people/" + encodeURIComponent(p.id) : null));
            li.appendChild(el("span", "ms-auto row-sub", p.region));
            online.appendChild(li);
        });
        online.hidden = s.online.length === 0;
        note.hidden = s.online.length > 0;
        note.textContent = s.onlineAvailable ? "Nobody is in-world right now." : "This list shows once the database is running.";
    }

    var dash = "—";
    function number(n) { return typeof n === "number" ? n.toLocaleString("en-US") : dash; }
    function km2(m2) { return typeof m2 === "number" ? (m2 / 1000000).toLocaleString("en-US", { minimumFractionDigits: 1, maximumFractionDigits: 1 }) : dash; }

    function renderCounts(d) {
        var c = d.counts;
        document.querySelectorAll("[data-n]").forEach(function (e) { e.textContent = number(c[e.getAttribute("data-n")]); });
        document.querySelectorAll("[data-km2]").forEach(function (e) { e.textContent = km2(c[e.getAttribute("data-km2")]); });
        var checks = document.getElementById("checks");
        var pill = { Working: ["state-running", "Working"], Slow: ["state-starting", "Slow"], CheckThis: ["state-starting", "Check this"], Down: ["state-failed", "Down"] };
        checks.textContent = "";
        (d.checks || []).forEach(function (k) {
            var li = document.createElement("li");
            var left = el("span");
            left.appendChild(el("span", "component-label", k.name));
            left.appendChild(document.createTextNode(" "));
            left.appendChild(el("span", "row-sub", k.detail));
            li.appendChild(left);
            li.appendChild(el("span", "ms-auto row-sub", typeof k.milliseconds === "number" ? k.milliseconds + " ms" : ""));
            var p = pill[k.state] || pill.Down;
            li.appendChild(el("span", "badge " + p[0], p[1]));
            checks.appendChild(li);
        });
        var at = document.getElementById("checked-at");
        var when = new Date(d.checkedAt);
        at.setAttribute("datetime", d.checkedAt);
        at.textContent = when.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
    }

    poll("/api/status", 5000, render);
    poll("/api/dashboard", 30000, renderCounts);
})();
