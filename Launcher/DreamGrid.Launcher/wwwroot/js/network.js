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

// "Find my address": the launcher asks the internet for this computer's public IP and fills it in. Without a script the
// button stays hidden and the owner types the address.
(function () {
    var button = document.getElementById("find-address");
    var input = document.getElementById("PublicAddress");
    if (!button || !input) return;
    var error = document.getElementById("address-error");
    var label = button.textContent;
    button.classList.remove("d-none");
    button.addEventListener("click", function () {
        button.disabled = true;
        button.textContent = "Finding…";
        fetch(button.dataset.url, { headers: { Accept: "application/json" }, credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : { error: "DreamGrid couldn't find this computer's public address. Type the address." }; })
            .catch(function () { return { error: "DreamGrid couldn't find this computer's public address. Type the address." }; })
            .then(function (result) {
                if (result.address) input.value = result.address;
                if (error) error.textContent = result.error || "";
                button.disabled = false;
                button.textContent = label;
            });
    });
})();
