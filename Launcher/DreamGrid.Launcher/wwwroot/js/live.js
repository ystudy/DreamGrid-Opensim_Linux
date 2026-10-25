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
