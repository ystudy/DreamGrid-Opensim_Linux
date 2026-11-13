// A form with data-confirm asks the question first; "Cancel" stops the post.
document.querySelectorAll("form[data-confirm]").forEach(function (form) {
    form.addEventListener("submit", function (e) {
        if (!window.confirm(form.getAttribute("data-confirm"))) e.preventDefault();
    });
});
