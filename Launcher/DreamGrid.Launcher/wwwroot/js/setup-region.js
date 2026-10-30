// Setup, Region: while the first region is being made, polls the progress and reloads when it finishes.
(function () {
    var stage = document.getElementById("setup-stage");
    var bar = document.getElementById("setup-bar");
    poll("/api/setup/progress", 2000, function (s) {
        if (!s.running) {
            // Finished or failed: the page itself shows the result.
            window.location.replace("/setup/region?working=1");
            return;
        }
        stage.textContent = (s.stage || "Getting ready") + "…";
        bar.value = s.percent;
        bar.textContent = s.percent + " %";
    });
})();
