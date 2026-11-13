// The add-region form: the size's memory sentence and the map position's placement sentence update as the person types.
// The form carries the risk sentences (data-risk, JSON) and the placement URL (data-placement).
(function () {
    var form = document.getElementById("add-form");
    if (!form) return;
    var risks = JSON.parse(form.dataset.risk);
    var text = document.getElementById("placement-text");
    var risk = document.getElementById("size-risk");
    var x = document.getElementById("X"), y = document.getElementById("Y"), size = document.getElementById("Size");
    var timer = null;

    function refresh() {
        risk.textContent = risks[size.value - 1] || "";
        if (x.value === "" || y.value === "") { text.textContent = ""; return; }
        clearTimeout(timer);
        timer = setTimeout(function () {
            fetch(form.dataset.placement + "?x=" + encodeURIComponent(x.value) + "&y=" + encodeURIComponent(y.value) + "&size=" + encodeURIComponent(size.value),
                { headers: { Accept: "application/json" }, credentials: "same-origin" })
                .then(function (r) { return r.json(); })
                .then(function (j) { text.textContent = j.text; })
                .catch(function () { /* the sentence refreshes on submit instead */ });
        }, 250);
    }
    [x, y, size].forEach(function (el) { el.addEventListener("change", refresh); el.addEventListener("input", refresh); });
})();
