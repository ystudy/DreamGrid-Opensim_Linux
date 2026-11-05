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
            navigator.clipboard.writeText(source.textContent).then(function () { button.textContent = "Copied"; }, select);
        } else {
            select();
            button.textContent = "Selected: press Ctrl+C";
        }
    });
}
