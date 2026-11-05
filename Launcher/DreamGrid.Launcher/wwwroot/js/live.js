// Live updates are an extra: every page works without JavaScript.
// poll(url, intervalMs, onJson) fetches url as JSON every intervalMs and passes the result to onJson.
// It returns a function that stops the polling.
function poll(url, intervalMs, onJson) {
    var stopped = false;
    function tick() {
        if (stopped) return;
        fetch(url, { headers: { Accept: "application/json" }, credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (json) { if (json !== null) onJson(json); })
            .catch(function () { /* the launcher may be restarting; try again next time */ })
            .then(function () { if (!stopped) setTimeout(tick, intervalMs); });
    }
    tick();
    return function stop() { stopped = true; };
}

// signInAgain() reloads to the sign-in page, which returns here: the admin's sign-in expired while the page was open.
function signInAgain() {
    window.location.href = "/login?ReturnUrl=" + encodeURIComponent(window.location.pathname + window.location.search);
}

// The Power and OAR menus are <details> elements, which stay open until their button is clicked again. A click
// outside an open menu closes it, and so does Escape (which puts the focus back on that menu's button).
function openMenus() {
    return document.querySelectorAll("details.power-menu[open], details.oar-menu[open]");
}
document.addEventListener("click", function (e) {
    openMenus().forEach(function (menu) {
        if (!menu.contains(e.target)) menu.open = false;
    });
});
document.addEventListener("keydown", function (e) {
    if (e.key !== "Escape") return;
    openMenus().forEach(function (menu) {
        menu.open = false;
        menu.querySelector("summary").focus();
    });
});
