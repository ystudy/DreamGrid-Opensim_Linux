// The add-region page while a region is being added: polls the progress and reloads when it finishes.
(function () {
    var stage = document.getElementById("add-stage");
    var bar = document.getElementById("add-bar");
    poll("/api/regions/progress", 2000, function (s) {
        if (!s.running) {
            // Finished or failed: the page itself shows the result.
            window.location.replace("/regions/add?working=1");
            return;
        }
        stage.textContent = (s.stage || "Getting ready") + "…";
        bar.value = s.percent;
        bar.textContent = s.percent + " %";
    });
})();
