// The dashboard: copy button, grid controls through /api/grid/*, and the live status (polled every 5 seconds).
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

    function render(s) {
        // The page was rendered for an admin whose sign-in has since expired.
        if (s.admin === false) { signInAgain(); return; }
        var headline = document.getElementById("headline");
        headline.textContent = s.headline;
        headline.className = "headline" + (s.problem ? " problem" : "");

        var list = document.getElementById("components");
        list.textContent = "";
        function row(spans) {
            var li = document.createElement("li");
            li.className = "list-group-item px-0";
            spans.forEach(function (p) {
                if (!p[1]) return;
                var span = document.createElement(p[2] ? "a" : "span");
                if (p[2]) span.href = p[2];
                span.className = p[0];
                span.textContent = p[1];
                li.appendChild(span);
                li.appendChild(document.createTextNode(" "));
            });
            list.appendChild(li);
        }
        [s.grid.database, s.grid.robust].forEach(function (c) {
            row([["component-label", c.label], ["badge state-" + c.state.toLowerCase(), c.stateText], ["text-body-secondary", c.detail]]);
        });
        s.grid.regions.forEach(function (r) {
            var others = r.runsWith && r.runsWith.length ? "runs with " + r.runsWith.join(", ") : "";
            row([["component-label", r.name, r.id ? "/regions?region=" + encodeURIComponent(r.id) : null], ["badge state-" + r.state.toLowerCase(), r.stateText], ["text-body-secondary", others]]);
        });

        var online = document.getElementById("online");
        var note = document.getElementById("online-note");
        online.textContent = "";
        s.online.forEach(function (name) {
            var li = document.createElement("li");
            li.textContent = name;
            online.appendChild(li);
        });
        online.hidden = s.online.length === 0;
        note.hidden = s.online.length > 0;
        note.textContent = s.onlineAvailable ? "Nobody is in-world right now." : "This list shows once the database is running.";
    }

    poll("/api/status", 5000, render);
})();
