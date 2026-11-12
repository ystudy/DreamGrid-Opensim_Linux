// The sign-up form in two screens (spec 2026-11-11-starter-avatars-design §5.1). Without this script both steps show on one
// page and still work. With it, step 1 shows first; "Next" checks step 1's fields in the browser and shows step 2; "Back" returns.
// The whole form is still sent once, by "Create my avatar".
(function () {
    var form = document.getElementById("sign-up");
    var step1 = document.getElementById("step1");
    var step2 = document.getElementById("step2");
    var next = document.getElementById("to-step2");
    var back = document.getElementById("to-step1");
    if (!form || !step1 || !step2 || !next || !back) return;

    function show(n, moveFocus) {
        step1.hidden = n !== 1;
        step2.hidden = n !== 2;
        form.setAttribute("data-step", String(n));
        var focus = n === 2 ? step2.querySelector("input[name=Avatar]:checked") || step2.querySelector("input[name=Avatar]") : step1.querySelector("input");
        if (moveFocus && focus) focus.focus();
    }

    next.hidden = false;
    back.hidden = false;
    next.addEventListener("click", function () {
        var fields = step1.querySelectorAll("input, select, textarea");
        for (var i = 0; i < fields.length; i++) {
            if (!fields[i].checkValidity()) { fields[i].reportValidity(); return; }
        }
        // Passwords that don't match, a taken name and the rest are the server's to say: it sends the page back on step 1.
        show(2, true);
    });
    back.addEventListener("click", function () { show(1, true); });
    show(form.getAttribute("data-step") === "2" ? 2 : 1, false);
})();
