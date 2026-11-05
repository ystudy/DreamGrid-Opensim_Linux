// Setup's Connect step and the guest Connect page: Copy buttons for the world's address and the grid manager settings.
[["login-uri", "copy-uri"], ["grid-settings", "copy-settings"]].forEach(function (pair) {
    var source = document.getElementById(pair[0]);
    var button = document.getElementById(pair[1]);
    if (source && button) copyButton(source, button);
});
