// Proof of work for the bot protection. Each input[data-challenge-url] is filled with an answer to a challenge the server made
// (ALTCHA's format): the number n from 0 to maxnumber for which SHA-256(salt + n) is the challenge. The form waits for the answer
// if it is sent early. If the challenge can't be fetched or solved the field stays empty and the server stops the post.

// Finds the number, or returns null. digest(bytes) resolves to the SHA-256 of the bytes as an ArrayBuffer.
async function dgPowSolve(ch, digest) {
    var encoder = new TextEncoder();
    var wanted = String(ch.challenge).toLowerCase();
    for (var n = 0; n <= ch.maxnumber; n++) {
        var hash = new Uint8Array(await digest(encoder.encode(ch.salt + n)));
        var hex = "";
        for (var i = 0; i < hash.length; i++) hex += (hash[i] < 16 ? "0" : "") + hash[i].toString(16);
        if (hex === wanted) return n;
    }
    return null;
}

function dgPowStart(field) {
    var form = field.form;
    if (!form) return;
    var state = "pending";
    var submitter = null;
    var wantsSubmit = false;

    async function fetchAndSolve() {
        state = "pending";
        field.value = "";
        try {
            var response = await fetch(field.getAttribute("data-challenge-url"), { headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error("no challenge");
            var ch = await response.json();
            var n = await dgPowSolve(ch, function (bytes) { return crypto.subtle.digest("SHA-256", bytes); });
            if (n === null) throw new Error("no answer");
            field.value = btoa(JSON.stringify({ algorithm: ch.algorithm, challenge: ch.challenge, number: n, salt: ch.salt, signature: ch.signature }));
            state = "ready";
            // A challenge is good for 10 minutes: a page left open longer gets a fresh one.
            window.setTimeout(fetchAndSolve, 8 * 60 * 1000);
        } catch (e) {
            state = "failed";
        }
        if (wantsSubmit) {
            wantsSubmit = false;
            form.requestSubmit(submitter);
        }
    }

    form.addEventListener("submit", function (e) {
        if (state !== "pending") return;
        e.preventDefault();
        submitter = e.submitter;
        wantsSubmit = true;
    });
    fetchAndSolve();
}

if (typeof document !== "undefined") {
    document.querySelectorAll("input[data-challenge-url]").forEach(dgPowStart);
}
