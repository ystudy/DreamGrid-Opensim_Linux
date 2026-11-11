// The language picker: choosing a language sends the form at once, so the button is not needed.
document.querySelectorAll('form.language-picker').forEach(function (form) {
    var button = form.querySelector('button');
    if (button) button.hidden = true;
    var select = form.querySelector('select');
    if (select) select.addEventListener('change', function () { form.submit(); });
});
