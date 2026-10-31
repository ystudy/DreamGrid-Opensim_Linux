// The Email page's pop-ups: a card opens its dialog as a modal; Cancel closes it. Without script the card's link
// (?edit=<key>) renders the dialog open instead.
document.querySelectorAll("a[data-dialog]").forEach(function (card) {
    card.addEventListener("click", function (e) {
        var dialog = document.getElementById("dlg-" + card.getAttribute("data-dialog"));
        if (!dialog || !dialog.showModal) return;
        e.preventDefault();
        dialog.showModal();
    });
});

document.querySelectorAll("dialog.email-dialog").forEach(function (dialog) {
    // A dialog the server rendered open (an error, a test result) becomes a modal like the others.
    if (dialog.hasAttribute("open") && dialog.showModal) {
        dialog.close();
        dialog.showModal();
    }
    dialog.querySelectorAll("[data-close]").forEach(function (cancel) {
        cancel.addEventListener("click", function (e) {
            e.preventDefault();
            dialog.close();
        });
    });
    var security = dialog.querySelector("select.email-security");
    var warning = dialog.querySelector(".none-warning");
    if (security && warning) {
        security.addEventListener("change", function () {
            warning.hidden = security.value !== "None";
        });
    }
});
