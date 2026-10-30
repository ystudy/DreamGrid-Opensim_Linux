// The OAR job page while the job runs: polls the job (the URL is in data-job-url) and reloads when it ends.
(function () {
    var box = document.getElementById("oar-job");
    if (!box) return;
    poll(box.dataset.jobUrl, 2000, function (job) {
        // Finished or failed: the page itself shows the result.
        if (!job.running) { window.location.reload(); return; }
        document.getElementById("job-stage").textContent = job.stage;
        document.getElementById("job-written").textContent = job.writtenText || "";
        document.getElementById("job-elapsed").textContent = job.elapsedText;
    });
})();
