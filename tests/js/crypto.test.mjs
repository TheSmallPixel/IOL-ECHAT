// Tests the REAL production JS crypto (the same echat-crypto.mjs the browser worker imports).
// Node 20+ exposes the WebCrypto API globally, identical to the browser; no npm deps needed.
// Run:  node --test tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    encryptAesGcm, decryptAesGcm, signHmac, verifyHmac, MAGIC_AES_GCM_V1,
    generateRsaOaepKeyPair, generateEcdsaKeyPair, exportSpki,
    wrapCek, unwrapCek, signEcdsa, verifyEcdsa, MAGIC_RSA_WRAP_V1,
} from '../../src/ECHAT.Client.App/wwwroot/js/echat-crypto.mjs';

const key = Uint8Array.from({ length: 32 }, (_, i) => i);
const aad = new TextEncoder().encode('echat-test-aad');

test('AES-GCM round-trip returns the original plaintext', async () => {
    const pt = new TextEncoder().encode('ciao mondo 🌍');
    const ct = await encryptAesGcm(pt, key, aad);

    assert.equal(ct[0], MAGIC_AES_GCM_V1, 'wire format starts with 0xA1 magic');
    assert.ok(ct.length >= 1 + 12 + 16, 'has magic + IV(12) + tag(16)');

    const back = await decryptAesGcm(ct, key, aad);
    assert.deepEqual([...back], [...pt]);
});

test('AES-GCM fresh IV per call: same plaintext -> different ciphertext', async () => {
    const pt = new Uint8Array([1, 2, 3, 4]);
    const a = await encryptAesGcm(pt, key, aad);
    const b = await encryptAesGcm(pt, key, aad);
    assert.notDeepEqual([...a], [...b]);
});

test('AES-GCM tampered ciphertext fails the auth tag', async () => {
    const ct = await encryptAesGcm(new Uint8Array([1, 2, 3]), key, aad);
    ct[ct.length - 1] ^= 0xff; // flip a tag byte
    await assert.rejects(() => decryptAesGcm(ct, key, aad));
});

test('AES-GCM wrong AAD fails the auth tag', async () => {
    const ct = await encryptAesGcm(new Uint8Array([1, 2, 3]), key, aad);
    await assert.rejects(() => decryptAesGcm(ct, key, new TextEncoder().encode('other-aad')));
});

test('AES-GCM rejects a buffer without the 0xA1 magic', async () => {
    const notCt = new Uint8Array(40); // all zeros, no magic
    await assert.rejects(() => decryptAesGcm(notCt, key, aad));
});

test('HMAC-SHA256 sign/verify round-trip', async () => {
    const data = new Uint8Array([1, 2, 3, 4, 5]);
    const sig = await signHmac(data, key);
    assert.equal(sig.length, 32);
    assert.equal(await verifyHmac(data, sig, key), true);
});

test('HMAC-SHA256 verify fails with the wrong key', async () => {
    const data = new Uint8Array([9, 9, 9]);
    const sig = await signHmac(data, key);
    const otherKey = new Uint8Array(32).fill(7);
    assert.equal(await verifyHmac(data, sig, otherKey), false);
});

// ── E2EE asymmetric primitives (S1-S4) ──────────────────────────────────────

test('RSA-OAEP wrapCek/unwrapCek round-trips a 32-byte CEK', async () => {
    const { publicKey, privateKey } = await generateRsaOaepKeyPair();
    const spki = await exportSpki(publicKey);
    const cek = crypto.getRandomValues(new Uint8Array(32));

    const blob = await wrapCek(cek, spki);
    assert.equal(blob[0], MAGIC_RSA_WRAP_V1, 'wrapped blob starts with 0xB2 magic');
    assert.ok(blob.length > 32 && blob.length <= 1 + 256, 'is an RSA wrap, not raw key bytes');

    const back = await unwrapCek(blob, privateKey);
    assert.deepEqual([...back], [...cek]);
});

test('unwrapCek rejects a blob without the 0xB2 magic', async () => {
    const { privateKey } = await generateRsaOaepKeyPair();
    await assert.rejects(() => unwrapCek(new Uint8Array(257), privateKey));
});

test('unwrapCek with the wrong private key fails', async () => {
    const a = await generateRsaOaepKeyPair();
    const b = await generateRsaOaepKeyPair();
    const cek = crypto.getRandomValues(new Uint8Array(32));
    const blob = await wrapCek(cek, await exportSpki(a.publicKey));
    await assert.rejects(() => unwrapCek(blob, b.privateKey));
});

test('ECDSA signEcdsa/verifyEcdsa round-trip over a 32-byte hash', async () => {
    const { publicKey, privateKey } = await generateEcdsaKeyPair();
    const spki = await exportSpki(publicKey);
    const hash = crypto.getRandomValues(new Uint8Array(32));

    const sig = await signEcdsa(hash, privateKey);
    assert.equal(sig.length, 64, 'ECDSA P-256 P1363 signature is 64 bytes (r‖s)');
    assert.equal(await verifyEcdsa(hash, sig, spki), true);
});

test('ECDSA verify fails on a tampered hash', async () => {
    const { publicKey, privateKey } = await generateEcdsaKeyPair();
    const spki = await exportSpki(publicKey);
    const hash = crypto.getRandomValues(new Uint8Array(32));
    const sig = await signEcdsa(hash, privateKey);
    hash[0] ^= 0xff;
    assert.equal(await verifyEcdsa(hash, sig, spki), false);
});

test('ECDSA verify fails against a different signer (forgery)', async () => {
    const signer = await generateEcdsaKeyPair();
    const impostor = await generateEcdsaKeyPair();
    const hash = crypto.getRandomValues(new Uint8Array(32));
    const sig = await signEcdsa(hash, signer.privateKey);
    assert.equal(await verifyEcdsa(hash, sig, await exportSpki(impostor.publicKey)), false);
});

// ── Key FILE backup/restore foundation ──────────────────────────────────────
// Le chiavi sono ora ESTRAIBILI così echat.js può esportarle in un file (PKCS#8) e reimportarle
// altrove. Questi test bloccano l'invariante: una coppia esportata→reimportata deve continuare a
// fare unwrap della CEK (RSA) e a firmare verificabile (ECDSA): è ciò su cui poggia il restore.

test('device keys are extractable (PKCS#8 export succeeds)', async () => {
    const rsa = await generateRsaOaepKeyPair();
    const ecdsa = await generateEcdsaKeyPair();
    const rsaPkcs8 = await crypto.subtle.exportKey('pkcs8', rsa.privateKey);
    const ecdsaPkcs8 = await crypto.subtle.exportKey('pkcs8', ecdsa.privateKey);
    assert.ok(rsaPkcs8.byteLength > 0, 'RSA private key exports to PKCS#8');
    assert.ok(ecdsaPkcs8.byteLength > 0, 'ECDSA private key exports to PKCS#8');
});

test('RSA key survives a PKCS#8 export→import round-trip (file restore) and still unwraps', async () => {
    const orig = await generateRsaOaepKeyPair();
    const spki = await exportSpki(orig.publicKey);
    const cek = crypto.getRandomValues(new Uint8Array(32));
    const blob = await wrapCek(cek, spki); // wrapped for the ORIGINAL device

    // Simulate exporting to a key file and re-importing it on another browser.
    const pkcs8 = await crypto.subtle.exportKey('pkcs8', orig.privateKey);
    const restored = await crypto.subtle.importKey(
        'pkcs8', pkcs8, { name: 'RSA-OAEP', hash: 'SHA-256' }, true, ['unwrapKey']);

    const back = await unwrapCek(blob, restored); // the restored key opens the old wrap
    assert.deepEqual([...back], [...cek]);
});

test('ECDSA key survives a PKCS#8 export→import round-trip and still signs verifiably', async () => {
    const orig = await generateEcdsaKeyPair();
    const spki = await exportSpki(orig.publicKey);
    const hash = crypto.getRandomValues(new Uint8Array(32));

    const pkcs8 = await crypto.subtle.exportKey('pkcs8', orig.privateKey);
    const restored = await crypto.subtle.importKey(
        'pkcs8', pkcs8, { name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign']);

    const sig = await signEcdsa(hash, restored);
    // The signature from the restored private key still verifies against the original public SPKI.
    assert.equal(await verifyEcdsa(hash, sig, spki), true);
});
