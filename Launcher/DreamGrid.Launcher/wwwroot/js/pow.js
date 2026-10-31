// Proof of work for the bot protection. Each input[data-challenge-url] is filled with an answer to a challenge the server made
// (ALTCHA's format): the number n from 0 to maxnumber for which SHA-256(salt + n) is the challenge. The form waits for the answer
// if it is sent early. If the challenge can't be fetched or solved the field stays empty and the server stops the post.

// SHA-256 of a Uint8Array, as a Uint8Array of 32 bytes. SubtleCrypto is only there on secure pages (https or localhost), and a grid on
// plain HTTP is not one, so this plain version stands in for it.
var DG_SHA256_K = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
];

function dgSha256(bytes) {
    var h = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
    var length = bytes.length;
    var padded = new Uint8Array(((length + 9 + 63) >> 6) << 6);
    padded.set(bytes);
    padded[length] = 0x80;
    var view = new DataView(padded.buffer);
    view.setUint32(padded.length - 8, Math.floor(length / 0x20000000), false);
    view.setUint32(padded.length - 4, (length << 3) >>> 0, false);
    var w = new Uint32Array(64);
    for (var offset = 0; offset < padded.length; offset += 64) {
        var t;
        for (t = 0; t < 16; t++) w[t] = view.getUint32(offset + t * 4, false);
        for (t = 16; t < 64; t++) {
            var x = w[t - 15], y = w[t - 2];
            var s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
            var s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
            w[t] = (w[t - 16] + s0 + w[t - 7] + s1) >>> 0;
        }
        var a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], k = h[7];
        for (t = 0; t < 64; t++) {
            var big1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
            var t1 = (k + big1 + ((e & f) ^ (~e & g)) + DG_SHA256_K[t] + w[t]) >>> 0;
            var big0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
            var t2 = (big0 + ((a & b) ^ (a & c) ^ (b & c))) >>> 0;
            k = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
        }
        h[0] = (h[0] + a) >>> 0; h[1] = (h[1] + b) >>> 0; h[2] = (h[2] + c) >>> 0; h[3] = (h[3] + d) >>> 0;
        h[4] = (h[4] + e) >>> 0; h[5] = (h[5] + f) >>> 0; h[6] = (h[6] + g) >>> 0; h[7] = (h[7] + k) >>> 0;
    }
    var out = new Uint8Array(32);
    var outView = new DataView(out.buffer);
    for (var i = 0; i < 8; i++) outView.setUint32(i * 4, h[i], false);
    return out;
}

// SubtleCrypto where the page has it, the plain version where it does not. Either may return its result or a promise of it.
function dgPowDigest() {
    var subtle = typeof crypto !== "undefined" && crypto && crypto.subtle;
    return subtle ? function (bytes) { return subtle.digest("SHA-256", bytes); } : dgSha256;
}

// Finds the number, or returns null. digest(bytes) gives (or resolves to) the SHA-256 of the bytes as an ArrayBuffer or Uint8Array.
// The loop hands control back to the page every few thousand tries so it stays responsive.
async function dgPowSolve(ch, digest) {
    var encoder = new TextEncoder();
    var wanted = String(ch.challenge).toLowerCase();
    for (var n = 0; n <= ch.maxnumber; n++) {
        if (n % 2000 === 1999) await new Promise(function (resolve) { setTimeout(resolve, 0); });
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
            var n = await dgPowSolve(ch, dgPowDigest());
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
