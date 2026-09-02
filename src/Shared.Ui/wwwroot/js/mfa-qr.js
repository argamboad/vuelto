// MFA enrollment QR rendering.
//
// Draws the authenticator `otpauth://` provisioning URI as a scannable QR code using
// the vendored qrcode-generator library (MIT, Kazuhiko Arase). Everything runs in the
// browser — the shared TOTP secret carried by the URI never leaves the client.
window.mfaQr = {
    // Renders `otpauthUri` as an inline SVG QR into the element with id `elementId`.
    // Returns false (and leaves the element empty) if the library is missing or the
    // data can't be encoded, so the Blazor side can fall back to manual-entry only.
    render: function (elementId, otpauthUri) {
        try {
            const el = document.getElementById(elementId);
            if (!el || typeof qrcode === "undefined" || !otpauthUri) return false;

            const qr = qrcode(0, "M"); // type 0 = auto-size to fit the data; ECC level M
            qr.addData(otpauthUri);
            qr.make();

            el.innerHTML = qr.createSvgTag({ cellSize: 4, margin: 4, scalable: true });
            return true;
        } catch (e) {
            console.error("mfaQr.render failed", e);
            return false;
        }
    },

    // Clears a previously rendered QR (used when enrollment is cancelled/confirmed).
    clear: function (elementId) {
        const el = document.getElementById(elementId);
        if (el) el.innerHTML = "";
    }
};
