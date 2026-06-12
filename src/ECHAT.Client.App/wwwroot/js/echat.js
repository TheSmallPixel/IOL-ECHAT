// ECHAT browser-side helpers, kept in a separate file so the static-asset cache busting
// query string actually works. (Inline <script> blocks in index.html don't get cache-busted
// and the browser will happily serve a stale copy after deploys.)

// ---------- Drop zone ----------
window.echatDropZone = (() => {
    let windowGuardInstalled = false;
    const ensureWindowGuard = () => {
        if (windowGuardInstalled) return;
        windowGuardInstalled = true;
        // Block default drag-and-drop on the page so a missed drop doesn't navigate the browser
        // to the dropped file (default browser behavior is "open the file as the new page").
        window.addEventListener('dragover', (e) => { e.preventDefault(); }, false);
        window.addEventListener('drop', (e) => { e.preventDefault(); }, false);
    };

    const wired = new WeakSet();

    return {
        attach: function (dropElId, inputElId) {
            const drop = document.getElementById(dropElId);
            // Fallback: in case Blazor's <InputFile> doesn't propagate the id, grab the hidden
            // file input by type. There's only one hidden file input inside the chat page.
            const input = document.getElementById(inputElId)
                || (drop ? drop.querySelector('input[type="file"]') : null);

            if (!drop || !input) {
                console.warn('[echat] dropZone.attach: missing element', {
                    drop: !!drop, input: !!input, dropElId, inputElId
                });
                return false;
            }
            if (wired.has(drop)) return true;
            wired.add(drop);

            ensureWindowGuard();
            console.log('[echat] dropZone.attach: wired', dropElId, '', input);

            let depth = 0;
            const hasFiles = (e) =>
                e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');

            const onEnter = (e) => {
                if (!hasFiles(e)) return;
                e.preventDefault();
                depth++;
                drop.classList.add('echat-dragging');
            };
            const onOver = (e) => {
                if (!hasFiles(e)) return;
                e.preventDefault();
                e.dataTransfer.dropEffect = 'copy';
            };
            const onLeave = (e) => {
                if (!hasFiles(e)) return;
                e.preventDefault();
                if (--depth <= 0) { depth = 0; drop.classList.remove('echat-dragging'); }
            };
            const onDrop = (e) => {
                if (!hasFiles(e)) return;
                e.preventDefault();
                e.stopPropagation();
                depth = 0;
                drop.classList.remove('echat-dragging');
                if (!e.dataTransfer.files || e.dataTransfer.files.length === 0) return;
                console.log('[echat] dropZone: dropped', e.dataTransfer.files.length, 'file(s)');
                try {
                    const dt = new DataTransfer();
                    for (const f of e.dataTransfer.files) dt.items.add(f);
                    input.files = dt.files;
                } catch (err) {
                    console.warn('[echat] could not assign input.files via DataTransfer, falling back', err);
                    input.files = e.dataTransfer.files;
                }
                input.dispatchEvent(new Event('change', { bubbles: true }));
            };

            drop.addEventListener('dragenter', onEnter);
            drop.addEventListener('dragover', onOver);
            drop.addEventListener('dragleave', onLeave);
            drop.addEventListener('drop', onDrop);
            return true;
        }
    };
})();

// ---------- Desktop notifications ----------
window.echatNotify = {
    requestPermission: async function () {
        if (!('Notification' in window)) return false;
        if (Notification.permission === 'granted') return true;
        if (Notification.permission === 'denied') return false;
        try {
            const result = await Notification.requestPermission();
            return result === 'granted';
        } catch { return false; }
    },
    /**
     * Show a desktop notification. Self-suppresses when the tab is already focused; the
     * sidebar badge + (optional) ding take over in that case (Slack-style behavior).
     */
    show: function (title, body) {
        if (!('Notification' in window)) return;
        if (Notification.permission !== 'granted') return;
        if (document.visibilityState === 'visible' && document.hasFocus()) return;
        try {
            const n = new Notification(title, { body: body, icon: '/icon-192.png', tag: 'echat-msg' });
            n.onclick = () => { window.focus(); n.close(); };
            setTimeout(() => n.close(), 5000);
        } catch { /* ignore */ }
    },
    setUnreadTitleBadge: function (totalUnread, originalTitle) {
        const base = originalTitle || 'ECHAT';
        document.title = totalUnread > 0 ? `(${totalUnread}) ${base}` : base;
    },

    /**
     * Plays a soft two-note chord-style "ding" via Web Audio. No audio file required.
     * The caller decides when to play; this function does NOT self-suppress on focus, so
     * MainLayout can choose to play (e.g. message in another conversation) or stay silent
     * (message in the active conversation while the window is focused, i.e. read on arrival).
     * Subject to browser autoplay rules: only fires after a prior user gesture on the page.
     */
    playSound: function () {
        try {
            const Ctx = window.AudioContext || window.webkitAudioContext;
            if (!Ctx) return;
            const ctx = window.__echatAudio || (window.__echatAudio = new Ctx());
            if (ctx.state === 'suspended') ctx.resume();

            const master = ctx.createGain();
            master.gain.value = 0.6;
            // Light low-pass: shaves off the sharp upper harmonics so the ding is "rounder".
            const filter = ctx.createBiquadFilter();
            filter.type = 'lowpass';
            filter.frequency.value = 3200;
            master.connect(filter).connect(ctx.destination);

            const now = ctx.currentTime;
            // C major chord-style arpeggio: soft and pleasing rather than the old sharp two-tone.
            //   D5 (587 Hz)  A5 (880 Hz)  F#5 (740 Hz, slight overlap to thicken the bell)
            const note = (freq, start, dur, peak) => {
                const osc = ctx.createOscillator();
                const gain = ctx.createGain();
                osc.type = 'sine';
                osc.frequency.value = freq;
                gain.gain.setValueAtTime(0.0001, now + start);
                gain.gain.exponentialRampToValueAtTime(peak, now + start + 0.015); // gentle attack
                gain.gain.exponentialRampToValueAtTime(0.0001, now + start + dur); // exp decay
                osc.connect(gain).connect(master);
                osc.start(now + start);
                osc.stop(now + start + dur + 0.02);
            };
            note(587.33, 0.00, 0.55, 0.18);   // D5
            note(880.00, 0.09, 0.55, 0.14);   // A5
            note(739.99, 0.18, 0.50, 0.10);   // F#5 (overlap)
        } catch { /* ignore: autoplay restrictions or no audio device */ }
    }
};

// ---------- Focus / visibility tracking ----------
// Used by MainLayout to drive Slack-style "read on arrival": a message in the currently-open
// conversation is only considered read if the window/tab is also focused. When focus returns
// the .NET handler clears the unread counter for whichever conversation is currently visible.
window.echatFocus = (() => {
    const refs = new Map();
    let nextId = 1;
    return {
        /** True iff the document is visible AND the window has OS focus. */
        isFocused: function () {
            return document.visibilityState === 'visible' && document.hasFocus();
        },
        /**
         * Subscribe to focus / visibility transitions. Returns an id to pass to remove().
         * The .NET callback is invoked with a single boolean: the new focused state.
         */
        onFocusChange: function (dotNetRef, methodName) {
            const id = nextId++;
            const handler = () => {
                try { dotNetRef.invokeMethodAsync(methodName, this.isFocused()); }
                catch (e) { console.warn('[echat] focus callback failed', e); }
            };
            const bound = handler.bind(window.echatFocus);
            window.addEventListener('focus', bound);
            window.addEventListener('blur', bound);
            document.addEventListener('visibilitychange', bound);
            refs.set(id, bound);
            return id;
        },
        removeFocusListener: function (id) {
            const h = refs.get(id);
            if (!h) return;
            window.removeEventListener('focus', h);
            window.removeEventListener('blur', h);
            document.removeEventListener('visibilitychange', h);
            refs.delete(id);
        }
    };
})();

// ---------- Scroll helpers ----------
// Conversation open often races with AttachmentPreview (image / iframe / blob:) loads that
// extend `scrollHeight` *after* our initial scroll. The "sticky" variant glues the view to
// the bottom for a short window so the message we just scrolled past doesn't get pushed up
// by an image that finished decoding 200ms later.
window.echatScroll = {
    toBottom: function (elId) {
        const el = document.getElementById(elId);
        if (el) el.scrollTop = el.scrollHeight;
    },
    /**
     * Returns true when the element is within `threshold` px of its bottom (default 150).
     * If the element doesn't exist, returns false; caller should treat "no container"
     * as "not visible", so a missing chat page never counts as "actively reading".
     */
    isNearBottom: function (elId, threshold) {
        const el = document.getElementById(elId);
        if (!el) return false;
        return (el.scrollHeight - el.scrollTop - el.clientHeight) < (threshold || 150);
    },
    /**
     * Combined state for OnScroll: top distance + nearBottom in one round-trip.
     * Saves a JS interop call per scroll event.
     */
    getState: function (elId, nearBottomThreshold) {
        const el = document.getElementById(elId);
        if (!el) return { exists: false, top: 0, nearBottom: false };
        return {
            exists: true,
            top: el.scrollTop,
            nearBottom: (el.scrollHeight - el.scrollTop - el.clientHeight) < (nearBottomThreshold || 150)
        };
    },
    /**
     * Re-scrolls the element to the bottom on every animation frame for `durationMs`,
     * unless the user manually scrolls away (in which case we stop and respect their wish).
     */
    toBottomSticky: function (elId, durationMs) {
        const el = document.getElementById(elId);
        if (!el) return;
        const end = Date.now() + (durationMs || 800);
        let lastScrollHeight = -1;
        let userScrolled = false;
        const onUserScroll = () => {
            // Distance from bottom *before* we yank it back. >40 px means the user moved.
            if (el.scrollHeight - el.scrollTop - el.clientHeight > 40 && lastScrollHeight !== -1)
                userScrolled = true;
        };
        el.addEventListener('wheel', onUserScroll, { passive: true });
        el.addEventListener('touchmove', onUserScroll, { passive: true });
        const tick = () => {
            if (!el.isConnected || userScrolled) {
                el.removeEventListener('wheel', onUserScroll);
                el.removeEventListener('touchmove', onUserScroll);
                return;
            }
            el.scrollTop = el.scrollHeight;
            lastScrollHeight = el.scrollHeight;
            if (Date.now() < end) requestAnimationFrame(tick);
            else {
                el.removeEventListener('wheel', onUserScroll);
                el.removeEventListener('touchmove', onUserScroll);
            }
        };
        requestAnimationFrame(tick);
    }
};

// ---------- Auto-grow textarea ----------
// Any textarea with `data-echat-autogrow="<maxPx>"` resizes itself to fit its content
// up to the given pixel cap (default 240). At/over the cap the textarea switches to
// internal scrolling so the parent layout doesn't keep growing. Driven by a single
// document-level `input` listener (capture phase) so we cover:
//   • the user typing,
//   • the markdown toolbar's `echatMarkdown.wrap` synthetic input event,
//   • paste / cut.
// Programmatic value changes from Blazor (e.g. clearing `_inputText` after send) don't
// fire `input`, so SendMessage in Chat.razor calls `echatAutogrow.fit` explicitly to
// snap the textarea back to its min height.
window.echatAutogrow = {
    fit: function (elOrId) {
        const el = typeof elOrId === 'string' ? document.getElementById(elOrId) : elOrId;
        if (!el || !el.matches || !el.matches('textarea')) return;
        const max = parseInt(el.dataset.echatAutogrow, 10) || 240;

        // Two-phase: GROW with content, SHRINK back to the CSS min-height when content gets smaller.
        //   1. Hide any existing scrollbar: a visible scrollbar reserves space and biases the
        //      scrollHeight reading we're about to take.
        //   2. Reset height to `auto` so the box collapses to its natural content height (the CSS
        //      `min-height` keeps it visually grounded; the browser already clamps to it).
        //   3. Read scrollHeight ONCE: that's our content height + vertical padding.
        //   4. If content fits within the CSS min-height, clear the inline height entirely so the
        //      textarea snaps back to the exact CSS default (rather than holding an explicit pixel
        //      value smaller than min-height; visually identical, but cleaner state).
        //      Otherwise pin to min(content, cap). At/over the cap the scrollbar comes back.
        el.style.overflowY = 'hidden';
        el.style.height = 'auto';
        const measured = el.scrollHeight;
        const cssMin = parseInt(getComputedStyle(el).minHeight, 10) || 0;
        if (measured <= cssMin) {
            // Falls back to CSS: `min-height: 88px` rules the rendered height again.
            el.style.height = '';
        } else {
            el.style.height = Math.min(measured, max) + 'px';
        }
        if (measured > max) el.style.overflowY = 'auto';
    }
};
document.addEventListener('input', (e) => {
    const t = e.target;
    if (t && t.matches && t.matches('textarea[data-echat-autogrow]')) {
        window.echatAutogrow.fit(t);
    }
}, true);

// ---------- Plain-mode Enter-to-send guard ----------
// The composer's textarea now hosts every format including Plain. With a textarea the
// browser inserts a newline on Enter *before* Blazor's @onkeydown handler sees the value,
// so a Plain "Enter to send" would carry a trailing \n into SendMessageAsync. We catch
// Enter (without Shift) on textareas tagged `data-echat-format="plain"` during the
// capture phase and preventDefault; Blazor's own @onkeydown still fires (we don't stop
// propagation), so the message gets sent as the user expects.
document.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' || e.shiftKey) return;
    const t = e.target;
    if (!t || !t.matches || !t.matches('textarea[data-echat-format="plain"]')) return;
    e.preventDefault();
}, true);

// ---------- Click-outside helper ----------
// Used by the format dropdown in the composer. Caller passes the dropdown's
// container element + a [JSInvokable] callback name; we attach a mousedown
// listener that fires the callback whenever a click lands outside the
// container, then returns a handle so the caller can remove the listener
// when the dropdown closes.
window.echatClickOutside = (() => {
    const handlers = new Map();
    let nextId = 1;
    return {
        on: function (containerEl, dotNetRef, methodName) {
            if (!containerEl) return 0;
            const id = nextId++;
            const handler = (e) => {
                if (!containerEl.isConnected) return;
                if (containerEl.contains(e.target)) return;
                try { dotNetRef.invokeMethodAsync(methodName); }
                catch (err) { console.warn('[echat] clickOutside callback failed', err); }
            };
            // mousedown (not click) so we close *before* the next button's click
            // handler fires; otherwise opening a different menu would race with
            // closing this one.
            document.addEventListener('mousedown', handler, true);
            handlers.set(id, handler);
            return id;
        },
        off: function (id) {
            const h = handlers.get(id);
            if (!h) return;
            document.removeEventListener('mousedown', h, true);
            handlers.delete(id);
        }
    };
})();

// ---------- Read-state bridge ----------
// MainLayout owns the unread counters. The Chat page can't reach into it directly, so we
// keep MainLayout's DotNetObjectReference here and expose a thin function for the chat
// page to call when the user scrolls back to the bottom of an active conversation.
window.echatRead = {
    _layoutRef: null,
    setLayoutRef: function (ref) { this._layoutRef = ref; },
    markRead: async function (convId) {
        if (!this._layoutRef || !convId) return;
        try { await this._layoutRef.invokeMethodAsync('MarkConversationRead', convId); }
        catch (e) { console.warn('[echat] markRead failed', e); }
    }
};

// ---------- Crypto worker bridge (AES-GCM, off main thread) ----------
window.echatCrypto = (() => {
    let worker = null;
    let nextId = 1;
    const pending = new Map();

    const ensureWorker = () => {
        if (worker) return worker;
        worker = new Worker('/js/crypto-worker.js', { type: 'module' });
        worker.onmessage = (e) => {
            const { id, ok, result, error } = e.data;
            const p = pending.get(id);
            if (!p) return;
            pending.delete(id);
            if (ok) p.resolve(result);
            else p.reject(new Error(error || 'crypto-worker failed'));
        };
        worker.onerror = (e) => {
            console.error('[echat] crypto-worker error', e);
            for (const p of pending.values()) p.reject(new Error('crypto-worker crashed: ' + e.message));
            pending.clear();
            worker = null;
        };
        return worker;
    };

    const post = (op, payload) => new Promise((resolve, reject) => {
        const id = nextId++;
        pending.set(id, { resolve, reject });
        ensureWorker().postMessage({ id, op, ...payload }, payload._transfers || []);
    });

    // ---- E2EE device keys (RSA-OAEP wrap + ECDSA sign), persisted in IndexedDB ----
    // I CryptoKey privati sono ESTRAIBILI così l'utente può esportarli in un file e ripristinare la
    // stessa identità altrove (export/importDeviceKey). Trade-off: un XSS riuscito potrebbe leggerne
    // i byte. Le primitive vivono in echat-crypto.mjs (versionato per bustare la cache dei moduli).
    const MJS_VER = '2026-06-09-keyfile';
    const mjs = () => import(`/js/echat-crypto.mjs?v=${MJS_VER}`);
    const toB64 = (u8) => btoa(String.fromCharCode(...u8));
    const fromB64 = (b64) => Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    // Short fingerprint (8 hex) of a key's SPKI, for diagnostics: lets us see at a glance whether the
    // key that WRAPPED a CEK is the same key that's trying to UNWRAP it.
    const fp = async (u8) => {
        const h = new Uint8Array(await crypto.subtle.digest('SHA-256', u8));
        return Array.from(h.slice(0, 4)).map(b => b.toString(16).padStart(2, '0')).join('');
    };

    const idb = () => new Promise((resolve, reject) => {
        const req = indexedDB.open('echat-keys', 1);
        req.onupgradeneeded = () => req.result.createObjectStore('device');
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
    const idbGet = async (key) => {
        const db = await idb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction('device', 'readonly').objectStore('device').get(key);
            tx.onsuccess = () => resolve(tx.result);
            tx.onerror = () => reject(tx.error);
        });
    };
    const idbPut = async (key, value) => {
        const db = await idb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction('device', 'readwrite').objectStore('device').put(value, key);
            tx.onsuccess = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    };

    const generateRecord = async () => {
        const m = await mjs();
        const rsa = await m.generateRsaOaepKeyPair();
        const ecdsa = await m.generateEcdsaKeyPair();
        return {
            deviceId: crypto.randomUUID(),
            rsaPrivate: rsa.privateKey,
            ecdsaPrivate: ecdsa.privateKey,
            rsaSpkiB64: toB64(await m.exportSpki(rsa.publicKey)),
            ecdsaSpkiB64: toB64(await m.exportSpki(ecdsa.publicKey)),
        };
    };

    // Verifica che la coppia RSA del device faccia round-trip wrapunwrap su sé stessa. Se fallisce,
    // la coppia è corrotta/inconsistente (tipicamente un residuo IndexedDB di una build precedente o
    // di un altro keypair): va rigenerata, altrimenti TUTTE le CEK risultano non-unwrappabili.
    const selfTest = async (record) => {
        try {
            const m = await mjs();
            const probe = crypto.getRandomValues(new Uint8Array(32));
            const wrapped = await m.wrapCek(probe, fromB64(record.rsaSpkiB64));
            const back = await m.unwrapCek(wrapped, record.rsaPrivate);
            return back.length === 32 && back.every((b, i) => b === probe[i]);
        } catch (e) {
            console.warn('[echat] device key self-test threw', e);
            return false;
        }
    };

    let deviceCache = null;
    let deviceInitPromise = null;
    let lastOrigin = null;   // 'generated' | 'regenerated' | 'loaded' | 'imported' (per il prompt di backup)
    const ensureDevice = async () => {
        if (deviceCache) return deviceCache;
        // Gate concurrent callers (es. enrollment + primo send insieme) su una sola init: senza,
        // due chiamate parallele genererebbero DUE coppie e una si perderebbe. Persistiamo in IndexedDB
        // PRIMA di cachare in memoria (durabilità), e auto-guariamo coppie corrotte/inconsistenti.
        if (deviceInitPromise) return deviceInitPromise;
        deviceInitPromise = (async () => {
            let record = await idbGet('keys');
            let origin = 'loaded';
            if (!record) { record = await generateRecord(); await idbPut('keys', record); origin = 'generated'; }

            const ok = await selfTest(record);
            if (!ok && origin === 'generated') {
                // Una coppia APPENA generata che non fa round-trip è anomala (problema ambientale):
                // rigenera una volta. Nessun dato perso: non c'erano ancora wrap.
                console.warn('[echat] freshly-generated device key failed self-test; regenerating once');
                record = await generateRecord();
                await idbPut('keys', record);
                origin = 'regenerated';
            } else if (!ok) {
                // Coppia ESISTENTE che non fa round-trip: NON rigeneriamo in automatico, distruggerebbe
                // l'accesso a TUTTE le CEK già wrappate per la vecchia chiave (orfanando i wrap, anche su
                // un eventuale falso negativo del self-test). L'utente decide via reset esplicito.
                console.error('[echat] EXISTING device key failed self-test. If you cannot read messages, run ' +
                    'window.echatCrypto.resetDevice() then reload to re-enroll (abandons keys wrapped for the old key).');
            }

            deviceCache = record;
            lastOrigin = origin;
            console.log(`[echat] device keys ${origin} (selfTest=${ok ? 'pass' : 'FAIL'}): id=${record.deviceId} ` +
                `rsaFp=${await fp(fromB64(record.rsaSpkiB64))} ecdsaFp=${await fp(fromB64(record.ecdsaSpkiB64))}`);
            return deviceCache;
        })();
        try {
            return await deviceInitPromise;
        } finally {
            deviceInitPromise = null;
        }
    };

    // ---- Key FILE backup/restore --------------------------------------------------------------
    // Il device key è un file che l'utente custodisce. Esportiamo deviceId + chiavi private (PKCS#8)
    // + SPKI pubbliche; reimportando lo stesso file su un altro browser si RIPRISTINA la stessa
    // identità, quindi i wrap CEK esistenti tornano leggibili (e due browser con lo stesso file =
    // multi-device). Il file è una chiave privata in chiaro: trattarlo come una chiave SSH.
    const KEYFILE_TYPE = 'echat-device-key';

    const exportDeviceKey = async () => {
        const d = await ensureDevice();
        // Le chiavi generate dal codice precedente erano NON estraibili: non si possono esportare.
        // Messaggio chiaro invece di un InvalidAccessError grezzo: l'utente fa resetDevice() per
        // ottenerne una nuova (estraibile), o ne nasce una al prossimo storage clear.
        if (d.rsaPrivate.extractable === false || d.ecdsaPrivate.extractable === false)
            throw new Error('This device key predates file backup and is non-exportable. ' +
                'Run window.echatCrypto.resetDevice() then reload to get an exportable key, then back it up.');
        const rsaPkcs8 = new Uint8Array(await crypto.subtle.exportKey('pkcs8', d.rsaPrivate));
        const ecdsaPkcs8 = new Uint8Array(await crypto.subtle.exportKey('pkcs8', d.ecdsaPrivate));
        return JSON.stringify({
            v: 1,
            type: KEYFILE_TYPE,
            deviceId: d.deviceId,
            rsaPrivatePkcs8B64: toB64(rsaPkcs8),
            ecdsaPrivatePkcs8B64: toB64(ecdsaPkcs8),
            rsaSpkiB64: d.rsaSpkiB64,
            ecdsaSpkiB64: d.ecdsaSpkiB64,
        });
    };

    const importDeviceKey = async (json) => {
        const j = typeof json === 'string' ? JSON.parse(json) : json;
        if (!j || j.type !== KEYFILE_TYPE || !j.deviceId
            || !j.rsaPrivatePkcs8B64 || !j.ecdsaPrivatePkcs8B64 || !j.rsaSpkiB64 || !j.ecdsaSpkiB64)
            throw new Error('Not a valid ECHAT device-key file.');

        const rsaPrivate = await crypto.subtle.importKey(
            'pkcs8', fromB64(j.rsaPrivatePkcs8B64), { name: 'RSA-OAEP', hash: 'SHA-256' }, true, ['unwrapKey']);
        const ecdsaPrivate = await crypto.subtle.importKey(
            'pkcs8', fromB64(j.ecdsaPrivatePkcs8B64), { name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign']);

        const record = {
            deviceId: j.deviceId, rsaPrivate, ecdsaPrivate,
            rsaSpkiB64: j.rsaSpkiB64, ecdsaSpkiB64: j.ecdsaSpkiB64,
        };
        // Round-trip wrapunwrap: rifiuta un file corrotto o con private/SPKI non corrispondenti
        // PRIMA di sovrascrivere la coppia locale.
        if (!(await selfTest(record)))
            throw new Error('Imported key failed self-test (corrupt file or mismatched key pair).');

        await idbPut('keys', record);
        deviceCache = record;
        lastOrigin = 'imported';
        console.log(`[echat] device key imported from file: id=${record.deviceId} ` +
            `rsaFp=${await fp(fromB64(record.rsaSpkiB64))} ecdsaFp=${await fp(fromB64(record.ecdsaSpkiB64))}`);
        return { deviceId: record.deviceId };
    };

    // Trigger a browser download of the current device key as a .json file.
    const downloadDeviceKeyFile = async () => {
        const d = await ensureDevice();
        const json = await exportDeviceKey();
        const blob = new Blob([json], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `echat-device-${d.deviceId}.json`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    };

    // Open a file picker and import the chosen key file. Resolves true on success, false if cancelled.
    const importDeviceKeyFromPicker = async () => new Promise((resolve, reject) => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = 'application/json,.json';
        input.onchange = async () => {
            try {
                const file = input.files && input.files[0];
                if (!file) { resolve(false); return; }
                await importDeviceKey(await file.text());
                resolve(true);
            } catch (e) { reject(e); }
        };
        input.click();
    });

    // Wire an element as a drag-and-drop target for the key file. Idempotent. On a successful drop
    // we reload so enrollment re-registers the restored identity and the message flow restarts.
    const attachKeyDrop = (elId) => {
        const el = document.getElementById(elId);
        if (!el) return false;                       // not rendered yet; caller retries next render
        if (el.dataset.echatKeyDropWired === '1') return true;
        el.dataset.echatKeyDropWired = '1';

        // Stop a MISSED drop from making the browser navigate to the file (default page behavior).
        if (!window.__echatDropNavGuard) {
            window.__echatDropNavGuard = true;
            window.addEventListener('dragover', (e) => e.preventDefault(), false);
            window.addEventListener('drop', (e) => e.preventDefault(), false);
        }

        const hasFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
        const hi = (on) => { el.style.background = on ? 'rgba(120,160,255,.25)' : ''; };

        el.addEventListener('dragover', (e) => {
            if (!hasFiles(e)) return;
            e.preventDefault(); e.stopPropagation();
            e.dataTransfer.dropEffect = 'copy'; hi(true);
        });
        el.addEventListener('dragleave', (e) => { e.preventDefault(); e.stopPropagation(); hi(false); });
        el.addEventListener('drop', async (e) => {
            e.preventDefault(); e.stopPropagation(); hi(false);
            const file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
            if (!file) return;
            try {
                await importDeviceKey(await file.text());
                location.reload();
            } catch (err) {
                console.error('[echat] key file drop failed', err);
                alert('Could not restore key: ' + (err && err.message ? err.message : err));
            }
        });
        console.log('[echat] key drop zone wired:', elId);
        return true;
    };

    return {
        /** Returns the AES-GCM ciphertext as Uint8Array. Resolves on the main thread. */
        encrypt: (plaintext, key, aad) => post('encrypt', {
            plaintext: plaintext, key: key, aad: aad,
            // transfer the plaintext buffer to avoid copying; caller mustn't reuse it after this.
            _transfers: [plaintext.buffer]
        }),
        decrypt: (ciphertext, key, aad) => post('decrypt', {
            ciphertext: ciphertext, key: key, aad: aad,
            _transfers: [ciphertext.buffer]
        }),

        // HMAC-SHA256 sign/verify. Small/cheap, so run inline on the main thread (no worker
        // round-trip), reusing the SAME shared crypto module the worker and the Node tests use.
        sign: async (data, key) => {
            const { signHmac } = await mjs();
            return signHmac(data, key);
        },
        verify: async (data, signature, key) => {
            const { verifyHmac } = await mjs();
            return verifyHmac(data, signature, key);
        },

        // ---- E2EE device key API (consumed by LocalStorageDeviceKeyStore via IJSRuntime) ----
        /** Ensure this device has RSA+ECDSA keypairs in IndexedDB; returns {deviceId, rsaSpkiB64, ecdsaSpkiB64}. */
        ensureDevice: async () => {
            const d = await ensureDevice();
            return { deviceId: d.deviceId, rsaSpkiB64: d.rsaSpkiB64, ecdsaSpkiB64: d.ecdsaSpkiB64 };
        },
        /** ECDSA-sign a 32-byte digest with this device's private key  64-byte P1363 sig. */
        signHash: async (hash) => {
            const d = await ensureDevice();
            const { signEcdsa } = await mjs();
            return signEcdsa(hash, d.ecdsaPrivate);
        },
        /** Verify an ECDSA P1363 signature over a digest against a signer's ECDSA SPKI. */
        verifySignature: async (hash, signature, signerEcdsaSpki) => {
            const { verifyEcdsa } = await mjs();
            return verifyEcdsa(hash, signature, signerEcdsaSpki);
        },
        /** RSA-OAEP wrap a 32-byte CEK for a recipient's RSA SPKI  0xB2|wrapped. */
        wrapCekFor: async (cek, recipientRsaSpki) => {
            const { wrapCek } = await mjs();
            return wrapCek(cek, recipientRsaSpki);
        },
        /** RSA-OAEP unwrap a 0xB2 CEK blob with this device's private key  raw 32-byte CEK. */
        unwrapCek: async (blob) => {
            const d = await ensureDevice();
            const { unwrapCek } = await mjs();
            try {
                return await unwrapCek(blob, d.rsaPrivate);
            } catch (e) {
                console.error(`[echat] unwrapCek failed: this device rsaFp=${await fp(fromB64(d.rsaSpkiB64))}; ` +
                    `the CEK was wrapped for a DIFFERENT key (stale wrap or device key changed since provisioning).`, e);
                throw e;
            }
        },
        /** Export this device's key as a JSON string (deviceId + private keys + public SPKIs). */
        exportDeviceKey: () => exportDeviceKey(),
        /** Import/restore a device key from a JSON string produced by exportDeviceKey. */
        importDeviceKey: (json) => importDeviceKey(json),
        /** Download the current device key as a .json file the user can keep to restore access. */
        downloadDeviceKeyFile: () => downloadDeviceKeyFile(),
        /** Open a file picker to restore a device key from a saved file. Returns true if imported. */
        importDeviceKeyFromPicker: () => importDeviceKeyFromPicker(),
        /** Wire an element (by id) as a drag-and-drop target for the key file. Idempotent; returns
         *  true once wired (false if the element isn't in the DOM yet so the caller can retry). */
        attachKeyDrop: (elId) => attachKeyDrop(elId),
        /** Origin of the current device key: 'generated' | 'regenerated' | 'loaded' | 'imported' | null.
         *  Used by the UI to prompt a backup right after a fresh key is created. */
        deviceOrigin: () => lastOrigin,
        /** Wipe this device's keypair from IndexedDB + memory (forces fresh enrollment on next use).
         *  Call window.echatCrypto.resetDevice() then reload to resync the client to a wiped server. */
        resetDevice: async () => {
            deviceCache = null;
            await new Promise((resolve) => {
                const req = indexedDB.deleteDatabase('echat-keys');
                req.onsuccess = req.onerror = req.onblocked = () => resolve();
            });
            console.log('[echat] device keypair reset; reload to re-enroll');
        }
    };
})();

// ---------- Blob URL helpers (cheaper than data: URLs for big media) ----------
window.echatBlob = {
    /** Returns a blob: URL for the given base64 + mime. Caller is responsible for revoke(). */
    create: function (base64, mimeType) {
        const binary = atob(base64);
        const len = binary.length;
        const bytes = new Uint8Array(len);
        for (let i = 0; i < len; i++) bytes[i] = binary.charCodeAt(i);
        const blob = new Blob([bytes], { type: mimeType || 'application/octet-stream' });
        return URL.createObjectURL(blob);
    },
    revoke: function (url) {
        try { URL.revokeObjectURL(url); } catch { /* ignore */ }
    }
};

// ---------- Code-block syntax highlighting ----------
// Markdig emits `<pre><code class="language-xxx">…</code></pre>` for tagged fences.
// MessageBubble.OnAfterRenderAsync calls applyAll(rootEl) once the markdown HTML is in the DOM;
// we hand each <code> off to highlight.js (vendored to /lib/highlight/) and stamp a `data-lang`
// on the <pre> so the small CSS badge in the corner shows the detected language.
window.echatHighlight = {
    applyAll: function (rootEl) {
        if (!rootEl || typeof hljs === 'undefined') return;
        const blocks = rootEl.querySelectorAll('pre > code');
        blocks.forEach((code) => {
            // Re-applying highlightElement on an already-highlighted node is a no-op for hljs ≥ 11
            // because it tags the element with `data-highlighted="yes"`. Skip explicitly to avoid
            // the warning it logs.
            if (code.dataset.highlighted === 'yes') return;
            try {
                let lang = '';
                // Markdig sets `class="language-xxx"`. Extract for the badge; let hljs read the
                // class itself to choose the language (or auto-detect when there's no language- prefix).
                const m = (code.className || '').match(/language-([\w-]+)/);
                if (m) lang = m[1];
                hljs.highlightElement(code);
                if (!lang) {
                    // hljs.highlightAuto wrote the chosen language onto the element via a class like
                    // `hljs language-yaml`. Pull it back out for the badge.
                    const after = (code.className || '').match(/language-([\w-]+)/);
                    lang = after ? after[1] : (code.dataset.highlightedLanguage || '');
                }
                if (lang) {
                    const pre = code.parentElement;
                    if (pre && pre.tagName === 'PRE') {
                        pre.classList.add('has-lang');
                        pre.dataset.lang = lang;
                    }
                }
            } catch (e) {
                // Highlighting is best-effort: if hljs trips on something weird, just leave the
                // code block as plain monospaced text rather than failing the whole render.
                console.warn('[echat] highlight failed', e);
            }
        });
    }
};

// ---------- Sandboxed HTML iframe helper ----------
// Used by MessageBubble to render `MessageFormat.Html` messages. Sets the iframe src to a blob:
// URL holding the HTML body, then auto-resizes the iframe height to match its content so the
// chat bubble doesn't show a scrollable rectangle of empty space.
window.echatHtmlFrame = {
    attach: function (frameEl, blobUrl) {
        if (!frameEl || !blobUrl) return;
        frameEl.src = blobUrl;
        const fit = () => {
            try {
                // Same-origin would let us measure scrollHeight; sandbox="" makes it cross-origin
                // (unique origin), so contentDocument is null. Fall back to a reasonable max-height
                // and let the iframe scroll if the HTML is tall.
                const doc = frameEl.contentDocument;
                if (doc && doc.body) {
                    frameEl.style.height = Math.min(800, doc.body.scrollHeight + 24) + 'px';
                } else {
                    frameEl.style.height = '320px';
                }
            } catch {
                frameEl.style.height = '320px';
            }
        };
        frameEl.addEventListener('load', fit, { once: true });
    }
};

// ---------- Markdown toolbar helper ----------
// Wraps the current selection inside a textarea with markdown delimiters. Used by the toolbar
// above the chat input (Bold / Italic / inline code / code block / heading / quote / list / link).
window.echatMarkdown = {
    wrap: function (elId, before, after, placeholder) {
        const el = document.getElementById(elId);
        if (!el) return null;
        const start = el.selectionStart;
        const end = el.selectionEnd;
        const sel = el.value.substring(start, end) || (placeholder || '');
        const wrapped = before + sel + after;
        el.setRangeText(wrapped, start, end, 'select');
        // Re-focus the element so the user keeps typing where they were.
        el.focus();
        // Tell Blazor about the new value (the change event triggers @bind:event="oninput").
        el.dispatchEvent(new Event('input', { bubbles: true }));
        return el.value;
    }
};

// ---------- Browser download from base64 bytes ----------
window.echatDownload = function (base64, fileName, mimeType) {
    const binary = atob(base64);
    const len = binary.length;
    const bytes = new Uint8Array(len);
    for (let i = 0; i < len; i++) bytes[i] = binary.charCodeAt(i);
    const blob = new Blob([bytes], { type: mimeType || 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName || 'download';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
};

// Register the PWA service worker once helpers are wired up. Used to live inline in index.html
// but CSP `script-src 'self'` blocks inline tags, so keep it in this external file instead.
if ('serviceWorker' in navigator) {
    try { navigator.serviceWorker.register('/service-worker.js'); }
    catch (e) { console.warn('[echat] service worker registration failed', e); }
}

console.log('[echat] helpers loaded');
