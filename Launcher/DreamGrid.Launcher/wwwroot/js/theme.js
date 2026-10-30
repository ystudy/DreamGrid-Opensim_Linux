// Bootstrap's dark theme follows the computer's setting; without JavaScript the page stays light.
// Loaded in <head> without defer, so the theme is set before the page paints.
(function () {
    var dark = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)");
    function apply() { document.documentElement.setAttribute("data-bs-theme", dark && dark.matches ? "dark" : "light"); }
    apply();
    if (dark && dark.addEventListener) dark.addEventListener("change", apply);
})();
