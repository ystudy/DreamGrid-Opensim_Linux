// Setup, Other grid: shows the addresses only for another grid; the server enforces it either way.
(function () {
    var other = document.getElementById("Grid-other");
    if (!other) return;
    function update() {
        document.getElementById("other-part").hidden = !other.checked;
    }
    document.querySelectorAll("input[name=Grid]").forEach(function (k) { k.addEventListener("change", update); });
    update();
})();
