// Single source of truth for ECHAT symmetric crypto: AES-256-GCM + HMAC-SHA256.
// Pure functions over WebCrypto (`crypto.subtle` / `crypto.getRandomValues`), the same API in the
// browser and in Node 20+, so the production code and the Node tests exercise IDENTICAL crypto.
//
// AEAD wire format: byte 0 = 0xA1 (magic) | bytes 1..12 = IV (12-byte GCM nonce) |
//                   bytes 13.. = SubtleCrypto output (ciphertext || 16-byte auth tag).

export const MAGIC_AES_GCM_V1 = 0xA1;
const IV_LEN = 12;
const TAG_LEN = 16;

async function importAesKey(rawKeyBytes, usage) {
    return crypto.subtle.importKey('raw', rawKeyBytes, { name: 'AES-GCM' }, false, usage);
}

async function importHmacKey(rawKeyBytes, usage) {
    return crypto.subtle.importKey('raw', rawKeyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, usage);
}

/** AES-GCM encrypt. Returns Uint8Array: 0xA1 | IV(12) | (ciphertext || tag). */
export async function encryptAesGcm(plaintext, keyBytes, aad) {
    const key = await importAesKey(keyBytes, ['encrypt']);
    const iv = crypto.getRandomValues(new Uint8Array(IV_LEN));

    const cipherBuf = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: aad }, key, plaintext);
    const cipher = new Uint8Array(cipherBuf);

    const out = new Uint8Array(1 + iv.length + cipher.length);
    out[0] = MAGIC_AES_GCM_V1;
    out.set(iv, 1);
    out.set(cipher, 1 + iv.length);
    return out;
}

/** AES-GCM decrypt of a 0xA1-prefixed buffer. Throws on bad magic or failed auth tag. */
export async function decryptAesGcm(combined, keyBytes, aad) {
    if (combined.length < 1 + IV_LEN + TAG_LEN || combined[0] !== MAGIC_AES_GCM_V1) {
        throw new Error('Not an AES-GCM v1 ciphertext');
    }
    const iv = combined.slice(1, 1 + IV_LEN);
    const cipher = combined.slice(1 + IV_LEN);

    const key = await importAesKey(keyBytes, ['decrypt']);
    const plainBuf = await crypto.subtle.decrypt({ name: 'AES-GCM', iv, additionalData: aad }, key, cipher);
    return new Uint8Array(plainBuf);
}

/** HMAC-SHA256 over `data`. Returns the 32-byte tag as Uint8Array. */
export async function signHmac(data, keyBytes) {
    const key = await importHmacKey(keyBytes, ['sign']);
    const sig = await crypto.subtle.sign('HMAC', key, data);
    return new Uint8Array(sig);
}

/** HMAC-SHA256 verify (constant-time inside WebCrypto). */
export async function verifyHmac(data, signature, keyBytes) {
    const key = await importHmacKey(keyBytes, ['verify']);
    return crypto.subtle.verify('HMAC', key, signature, data);
}

// ─────────────────────────────────────────────────────────────────────────────
// E2EE asymmetric primitives (S1-S4 redesign).
//   - CEK wrap: RSA-OAEP-2048 / SHA-256. Wire: 0xB2 | rsaWrap(~256 bytes).
//   - Signatures: ECDSA P-256 / SHA-256, IEEE-P1363 (r‖s, 64 bytes), NOT DER.
// Private keys are generated EXTRACTABLE so the user can export them to a key FILE and restore the
// same device identity in another browser / after clearing storage (echat.js export/importDeviceKey).
// Trade-off vs the old non-extractable keys: a successful XSS could now also read the raw key bytes
// (before, it could only USE the key while the page was live). The key file itself is an unprotected
// private key: treat it like an SSH/wallet key; password-encrypting the file is a future option.
// ─────────────────────────────────────────────────────────────────────────────

/** Magic byte prefixing an RSA-OAEP-wrapped CEK blob, for wire-format agility. */
export const MAGIC_RSA_WRAP_V1 = 0xB2;

/** Per-device RSA-OAEP-2048 keypair used to wrap/unwrap the conversation key (CEK). Extractable so it
 *  can be exported to a key file (see echat.js exportDeviceKey) and re-imported elsewhere. */
export async function generateRsaOaepKeyPair() {
    return crypto.subtle.generateKey(
        { name: 'RSA-OAEP', modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: 'SHA-256' },
        true, ['wrapKey', 'unwrapKey']);
}

/** Per-device ECDSA P-256 keypair used to sign/verify message envelopes. Extractable (file backup). */
export async function generateEcdsaKeyPair() {
    return crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
}

/** Export a public key as SPKI DER (Uint8Array) for transport/storage in the directory. */
export async function exportSpki(publicKey) {
    return new Uint8Array(await crypto.subtle.exportKey('spki', publicKey));
}

/** Wrap a raw 32-byte CEK for a recipient (RSA-OAEP). Returns 0xB2 | wrapped(~256 bytes). */
export async function wrapCek(cekRaw, recipientRsaSpki) {
    const pub = await crypto.subtle.importKey('spki', recipientRsaSpki, { name: 'RSA-OAEP', hash: 'SHA-256' }, false, ['wrapKey']);
    const aes = await crypto.subtle.importKey('raw', cekRaw, { name: 'AES-GCM' }, true, ['encrypt', 'decrypt']);
    const wrapped = new Uint8Array(await crypto.subtle.wrapKey('raw', aes, pub, { name: 'RSA-OAEP' }));
    const out = new Uint8Array(1 + wrapped.length);
    out[0] = MAGIC_RSA_WRAP_V1;
    out.set(wrapped, 1);
    return out;
}

/** Unwrap a 0xB2-prefixed RSA-OAEP CEK blob with this device's private key. Returns the raw 32-byte CEK. */
export async function unwrapCek(blob, rsaPrivateKey) {
    // RSA-OAEP-2048 ciphertext is exactly 256 bytes; + 1 magic byte = 257. Reject anything that
    // can't be a valid wrap BEFORE handing it to WebCrypto, and convert DOMExceptions (corrupted/
    // tampered blob from a byzantine server) into a descriptive Error instead of letting them escape.
    if (!blob || blob.length !== 257 || blob[0] !== MAGIC_RSA_WRAP_V1)
        throw new Error(`Malformed wrapped CEK (len=${blob ? blob.length : 'null'}, expected 257 with magic 0xB2).`);
    try {
        const aes = await crypto.subtle.unwrapKey(
            'raw', blob.slice(1), rsaPrivateKey,
            { name: 'RSA-OAEP' }, { name: 'AES-GCM' }, true, ['encrypt', 'decrypt']);
        return new Uint8Array(await crypto.subtle.exportKey('raw', aes));
    } catch (e) {
        throw new Error('CEK unwrap failed (wrong key or corrupted wrap): ' + (e && e.message ? e.message : e));
    }
}

/** ECDSA P-256 sign over a 32-byte hash (the EnvelopeHasher digest). Returns 64-byte r‖s (P1363). */
export async function signEcdsa(hash, ecdsaPrivateKey) {
    return new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, ecdsaPrivateKey, hash));
}

/** Verify an ECDSA P-256 (P1363) signature over a 32-byte hash against a signer's SPKI public key.
 *  Fail-closed: a malformed SPKI / bad signature returns false (never throws), mirroring the server's
 *  EcdsaVerifier.VerifyP1363, so a corrupted directory entry can't crash the receive path. */
export async function verifyEcdsa(hash, signature, signerEcdsaSpki) {
    try {
        const pub = await crypto.subtle.importKey('spki', signerEcdsaSpki, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
        return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, pub, signature, hash);
    } catch {
        return false;
    }
}
