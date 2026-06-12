// Web Worker for AES-GCM encrypt / decrypt of message + file payloads.
// Runs entirely off the main thread so the UI stays responsive even on multi-MB files.
// The crypto itself lives in the shared module `echat-crypto.mjs` (same code the Node tests run);
// this file is only the worker message plumbing. Loaded as a module worker (type: "module").

import { encryptAesGcm, decryptAesGcm } from './echat-crypto.mjs';

self.onmessage = async (e) => {
    const { id, op, plaintext, ciphertext, key, aad } = e.data;
    try {
        if (op === 'encrypt') {
            const result = await encryptAesGcm(plaintext, key, aad);
            // Transfer the buffer (zero-copy) instead of structured-cloning.
            self.postMessage({ id, ok: true, result }, [result.buffer]);
        } else if (op === 'decrypt') {
            const result = await decryptAesGcm(ciphertext, key, aad);
            self.postMessage({ id, ok: true, result }, [result.buffer]);
        } else {
            self.postMessage({ id, ok: false, error: 'unknown op: ' + op });
        }
    } catch (err) {
        self.postMessage({ id, ok: false, error: (err && err.message) || String(err) });
    }
};
