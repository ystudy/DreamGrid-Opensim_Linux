// The library's upload with a progress bar. Without JavaScript the form posts as it is and the browser shows its own
// progress. The antiforgery token goes in the RequestVerificationToken header; the answer is JSON { ok, name, text }.
(function () {
    var form = document.getElementById("upload");
    if (!form || !window.XMLHttpRequest || !window.FormData) return;
    var file = form.querySelector("input[name=oar]");
    var button = form.querySelector("button[type=submit]");
    var progress = document.getElementById("upload-progress");
    var bar = progress.querySelector(".progress-bar");
    var status = document.getElementById("upload-status");

    function show(percent) {
        progress.hidden = false;
        progress.setAttribute("aria-valuenow", String(percent));
        bar.style.width = percent + "%";
        bar.textContent = percent + "%";
    }

    function finish(text) {
        progress.hidden = true;
        button.disabled = false;
        status.textContent = text;
    }

    form.addEventListener("submit", function (e) {
        if (!file.files || file.files.length === 0) return; // the browser asks for a file
        e.preventDefault();
        var token = form.querySelector("input[name=__RequestVerificationToken]").value;
        var xhr = new XMLHttpRequest();
        xhr.open("POST", form.action);
        xhr.setRequestHeader("RequestVerificationToken", token);
        xhr.setRequestHeader("X-Requested-With", "fetch");
        xhr.upload.onprogress = function (ev) {
            if (ev.lengthComputable) show(Math.floor(ev.loaded * 100 / ev.total));
        };
        xhr.onload = function () {
            if (xhr.status === 401) { signInAgain(); return; }
            var answer = null;
            try { answer = JSON.parse(xhr.responseText); } catch (_) { /* not JSON: a failure page */ }
            if (answer && answer.ok) {
                // The page lists the new file and shows the notice.
                window.location.href = "/regions/library";
                return;
            }
            finish(answer && answer.text ? answer.text : "The upload didn't work. Try again.");
        };
        xhr.onerror = function () { finish("The upload stopped before it finished. Try again."); };
        button.disabled = true;
        status.textContent = "Uploading " + file.files[0].name + "…";
        show(0);
        xhr.send(new FormData(form));
    });
})();
