// Setup, Network: shows the part that matches the address choice; the server enforces it either way.
// The parts that exist depend on the plan, so each is looked up and skipped when absent.
(function () {
    var pub = document.getElementById("AddressKind-public");
    if (!pub) return;
    function show(id, visible) {
        var el = document.getElementById(id);
        if (el) el.hidden = !visible;
    }
    function update() {
        var isPublic = pub.checked;
        show("lan-part", !isPublic);
        show("public-part", isPublic);
        show("switch-part", isPublic);
        show("lan-hypergrid", !isPublic);
        show("advisories-lan", !isPublic);
        show("advisories-public", isPublic);
    }
    document.querySelectorAll("input[name=AddressKind]").forEach(function (k) { k.addEventListener("change", update); });
    update();
})();
