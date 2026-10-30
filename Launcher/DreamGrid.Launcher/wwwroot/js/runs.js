// "How it runs" choices: only the chosen choice's "will restart" box matters.
(function () {
    document.querySelectorAll(".runs-choices").forEach(function (set) {
        function boxes() {
            var chosen = set.querySelector("input[name=Group]:checked");
            set.querySelectorAll("[data-group]").forEach(function (g) {
                var box = g.querySelector(".confirm");
                if (box) box.hidden = !chosen || g.getAttribute("data-group") !== chosen.value;
            });
        }
        set.querySelectorAll("input[name=Group]").forEach(function (r) { r.addEventListener("change", boxes); });
        boxes();
    });
})();
