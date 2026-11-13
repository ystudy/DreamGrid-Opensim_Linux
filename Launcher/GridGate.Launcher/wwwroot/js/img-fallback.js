// An image marked data-remove-on-error is removed when it does not load (a type's picture that is not installed),
// so the swatch behind it shows. The error event does not bubble, so it is caught in the capture phase; images that
// failed before this script ran are found by their state.
(function () {
    document.addEventListener("error", function (e) {
        var img = e.target;
        if (img && img.tagName === "IMG" && img.hasAttribute("data-remove-on-error")) img.remove();
    }, true);
    document.querySelectorAll("img[data-remove-on-error]").forEach(function (img) {
        if (img.complete && img.naturalWidth === 0) img.remove();
    });
})();
