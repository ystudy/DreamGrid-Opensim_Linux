// The Logs page: optional refresh every 5 seconds for the chosen log.
(function () {
    var auto = document.getElementById("log-auto");
    var pre = document.getElementById("log-lines");
    var note = document.getElementById("log-note");
    var stop = null;
    function url() {
        return "/api/logs?source=" + encodeURIComponent(document.getElementById("log-source").value) +
            "&level=" + encodeURIComponent(document.getElementById("log-level").value);
    }
    function show(tail) {
        var atEnd = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 20;
        pre.textContent = tail.lines.join("\n");
        note.textContent = tail.truncated ? "Showing the last " + tail.lines.length + " lines." : "";
        if (atEnd) pre.scrollTop = pre.scrollHeight;
    }
    auto.addEventListener("change", function () {
        if (stop) { stop(); stop = null; }
        if (auto.checked) stop = poll(url(), 5000, show);
    });
    // A changed picker restarts the polling for the new log.
    ["log-source", "log-level"].forEach(function (id) {
        document.getElementById(id).addEventListener("change", function () { if (auto.checked) auto.dispatchEvent(new Event("change")); });
    });
    pre.scrollTop = pre.scrollHeight;
})();
