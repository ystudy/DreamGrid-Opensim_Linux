// Loaded by both layouts, so the guest pages have it too (they don't load live.js).
// copyButton(source, button) shows button, which copies the text of source. The clipboard API needs a secure
// context (localhost); elsewhere the text is selected for Ctrl+C.
function copyButton(source, button) {
    function select() {
        var range = document.createRange();
        range.selectNodeContents(source);
        var sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
    }
    button.hidden = false;
    button.addEventListener("click", function () {
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(source.textContent).then(function () { setButtonText(button, "Copied"); }, select);
        } else {
            select();
            setButtonText(button, "Selected: press Ctrl+C");
        }
    });
}

// setButtonText(button, text) changes a button's words and keeps the icon the server put at its start (ButtonIconTagHelper).
function setButtonText(button, text) {
    var icon = button.querySelector("svg.btn-icon");
    button.textContent = text;
    if (icon) button.insertBefore(icon, button.firstChild);
}
