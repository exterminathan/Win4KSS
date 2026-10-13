// docs/browser-mock/shim.js
//
// Dev-only stand-in for window.chrome.webview, so the real production UI
// (src/Win4K.ScreenSaver/Browser/ui/{index.html,app.css,app.js}) can be opened in any
// browser and exercised without WebView2 or a compiled host. Loaded only by
// docs/browser-mock/dev.html -- app.js never references this file and must keep working
// unchanged when the real WebView2 bridge replaces it.
//
// What it does:
//   1. Implements the two methods app.js actually calls: chrome.webview.postMessage(obj)
//      (page -> host) and chrome.webview.addEventListener('message', fn) (host -> page).
//   2. Replays docs/browser-mock/fixture.json as a sequence of host -> page messages, in
//      the order and with the delays the fixture specifies.
//   3. Answers page -> host messages the way BrowserBridge.cs is specced to:
//      requestPreviews synthesizes previewReady from the small sample files under
//      docs/browser-mock/assets/ (round-robined, so every clip gets a plausible-looking
//      still/hover); setPinned simulates PinManager's budget check against the fixture's
//      cacheStats so the pinRefused path is reachable; setRotation/saveSettings/
//      refreshCatalogs/startNow just log and, where it matters for the UI, update local
//      mock state and push a follow-up stateDelta/settings/catalog message.
//
// This file intentionally knows nothing about the DOM. It is pure message plumbing, same
// as the real bridge is pure JSON plumbing over PostWebMessageAsJson/WebMessageReceived.

(function () {
  'use strict';

  const FIXTURE_URL = 'fixture.json';
  const ASSETS = 'assets/';
  const STILLS = ['still-0.jpg', 'still-1.jpg', 'still-2.jpg', 'still-3.jpg'];
  const HOVERS = ['hover-a.mp4', 'hover-b.mp4'];

  /** @type {Array<(ev: {data: any}) => void>} */
  const listeners = [];

  // Mutable mock-side state, seeded from the fixture once it loads.
  const mock = {
    settings: null,
    cacheStats: null,
    clipsByKey: new Map(), // key -> catalog clip record
    stateByKey: new Map(), // key -> stateDelta record
    previewVersion: 0,
  };

  function log(direction, type, payload) {
    // eslint-disable-next-line no-console
    console.log(`%c[shim] ${direction} ${type}`, direction === 'page→host' ? 'color:#9cf' : 'color:#9f9', payload);
  }

  function dispatch(message) {
    log('host→page', message.type, message);
    for (const fn of listeners.slice()) {
      try {
        fn({ data: message });
      } catch (err) {
        // eslint-disable-next-line no-console
        console.error('[shim] a message listener threw', err);
      }
    }
  }

  function clipMatchesSourceId(clip, sourceId) {
    return clip.sourceId === sourceId || (clip.sourceIds || []).includes(sourceId);
  }

  function randomAsset(list, key) {
    // Deterministic per key, so reloading the page doesn't shuffle which sample a card gets.
    let hash = 0;
    for (let i = 0; i < key.length; i++) hash = (hash * 31 + key.charCodeAt(i)) >>> 0;
    return list[hash % list.length];
  }

  function placeholderColorFor(category) {
    // Mirrors MediaPreviewService.PlaceholderColor closely enough for a dev harness: a
    // stable, muted, dark-enough-for-white-text tint keyed by category name.
    const name = (category || 'Other').toLowerCase();
    let hash = 2166136261;
    for (let i = 0; i < name.length; i++) {
      hash = (hash ^ name.charCodeAt(i)) * 16777619;
      hash >>>= 0;
    }
    const hue = (hash % 360) / 360;
    return hslToHex(hue, 0.35, 0.3);
  }

  function hslToHex(h, s, l) {
    function channel(t) {
      t = ((t % 1) + 1) % 1;
      const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
      const p = 2 * l - q;
      if (t < 1 / 6) return p + (q - p) * 6 * t;
      if (t < 0.5) return q;
      if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
      return p;
    }
    const toByte = (v) => Math.round(Math.max(0, Math.min(1, v)) * 255);
    const r = toByte(channel(h + 1 / 3));
    const g = toByte(channel(h));
    const b = toByte(channel(h - 1 / 3));
    return `#${[r, g, b].map((x) => x.toString(16).padStart(2, '0')).join('')}`;
  }

  function sendPreviewReady(key, kind) {
    const clip = mock.clipsByKey.get(key);
    const color = placeholderColorFor(clip ? clip.category : null);
    const caps = mock.settings && mock.settings.previewCapabilities;
    const ffmpegAvailable = !caps || caps.ffmpegAvailable !== false;

    if (kind === 'Hover' && !ffmpegAvailable) {
      dispatch({ type: 'previewReady', key, kind, status: 'Unavailable', url: null, placeholderColor: color });
      return;
    }

    mock.previewVersion += 1;
    const file = kind === 'Hover' ? randomAsset(HOVERS, key) : randomAsset(STILLS, key);
    const url = `${ASSETS}${file}?v=${mock.previewVersion}`;
    dispatch({ type: 'previewReady', key, kind, status: 'Ready', url, placeholderColor: color });
  }

  function mergeStateDelta(partial) {
    for (const [key, patch] of Object.entries(partial)) {
      const current = mock.stateByKey.get(key) || {};
      mock.stateByKey.set(key, Object.assign({}, current, patch));
    }
    dispatch({ type: 'stateDelta', clips: partial });
  }

  // ---- page -> host --------------------------------------------------------------------

  function handlePageMessage(msg) {
    log('page→host', msg.type, msg);
    switch (msg.type) {
      case 'setRotation': {
        const patch = {};
        for (const key of msg.keys) patch[key] = { inRotation: !!msg.value };
        mergeStateDelta(patch);
        break;
      }

      case 'setAutoAdd': {
        // Mirrors Rotation.SetAutoAdd: toggles membership in settings.autoAddSources, and
        // when turning it on with includeExisting also puts every existing matching clip
        // that isn't already in rotation into rotation (Rotation.SetInRotation over
        // Rotation.KeysOf(catalogue, sourceId)).
        const current = new Set(mock.settings.autoAddSources || []);
        if (msg.value) current.add(msg.sourceId); else current.delete(msg.sourceId);
        mock.settings.autoAddSources = Array.from(current);
        dispatch({ type: 'settings', values: mock.settings });

        if (msg.value && msg.includeExisting) {
          const patch = {};
          for (const [key, clip] of mock.clipsByKey) {
            if (!clipMatchesSourceId(clip, msg.sourceId)) continue;
            if (mock.stateByKey.get(key)?.inRotation) continue;
            patch[key] = { inRotation: true };
          }
          if (Object.keys(patch).length > 0) mergeStateDelta(patch);
        }
        break;
      }

      case 'setPinned': {
        const UNKNOWN_BYTES = 350 * 1024 * 1024;
        const estimate = (key) => (mock.clipsByKey.get(key) || {}).sizeBytes || UNKNOWN_BYTES;

        if (!msg.value) {
          const patch = {};
          for (const key of msg.keys) {
            const wasPinned = mock.stateByKey.get(key)?.pinned;
            const wasCached = mock.stateByKey.get(key)?.cached;
            patch[key] = { pinned: false, download: null };
            if (wasPinned) {
              mock.cacheStats.pinnedBytes = Math.max(0, mock.cacheStats.pinnedBytes - estimate(key));
              if (wasCached) mock.cacheStats.rollingBytes += estimate(key);
            }
          }
          mergeStateDelta(patch);
          dispatch(mock.cacheStats);
          break;
        }

        // Fake PinManager.CanPin: everything already pinned, plus the newly requested keys
        // that are not already pinned, each at sizeBytes if known else PinManager's
        // UnknownClipBytes guess, must fit the budget.
        let required = mock.cacheStats.pinnedBytes;
        for (const key of new Set(msg.keys)) {
          if (mock.stateByKey.get(key)?.pinned) continue;
          required += estimate(key);
        }

        const budget = mock.cacheStats.budgetBytes;
        if (required > budget) {
          const requiredGb = Math.ceil(required / (1024 * 1024 * 1024));
          const budgetGb = Math.round(budget / (1024 * 1024 * 1024));
          dispatch({
            type: 'pinRefused',
            keys: msg.keys,
            requiredGb,
            budgetGb,
            shortfallGb: Math.max(0, requiredGb - budgetGb),
          });
          break;
        }

        const patch = {};
        for (const key of msg.keys) {
          if (!mock.stateByKey.get(key)?.pinned) mock.cacheStats.pinnedBytes += estimate(key);
          patch[key] = {
            pinned: true,
            download: { state: 'Downloading', fraction: 0, bytesReceived: 0, totalBytes: estimate(key) },
          };
        }
        mergeStateDelta(patch);
        dispatch(mock.cacheStats);

        // Simulate the download finishing a couple of seconds later.
        for (const key of msg.keys) {
          setTimeout(() => mergeStateDelta({ [key]: { pinned: true, cached: true, download: null } }), 2000 + Math.random() * 2000);
        }
        break;
      }

      case 'selectClip':
        // No native preview panel in a plain browser; app.js already knows not to expect
        // one outside WebView2. Nothing to simulate.
        break;

      case 'panelRect':
        // Ditto -- logged above, nothing else to do.
        break;

      case 'requestPreviews': {
        const kinds = msg.kinds === 'Both' ? ['Still', 'Hover'] : [msg.kinds];
        for (const key of msg.keys) {
          for (const kind of kinds) {
            // A small random delay so the UI's "Pending" state is actually visible.
            setTimeout(() => sendPreviewReady(key, kind), 80 + Math.random() * 260);
          }
        }
        break;
      }

      case 'saveSettings': {
        Object.assign(mock.settings, msg.patch);
        dispatch({ type: 'settings', values: mock.settings });
        break;
      }

      case 'purgeCache': {
        // Not in docs/browser-bridge-contract.md's page→host table as written -- see the
        // TODO(local) next to app.js's "Purge rolling cache" button. Simulated here anyway so
        // the control is exercisable in the dev harness: VideoCache.PurgeUnpinned() deletes
        // every non-pinned cached file, so rollingBytes drops to zero and those clips' `cached`
        // flips false.
        const patch = {};
        for (const [key, state] of mock.stateByKey) {
          if (state.cached && !state.pinned) patch[key] = { cached: false };
        }
        if (Object.keys(patch).length > 0) mergeStateDelta(patch);
        mock.cacheStats.rollingBytes = 0;
        dispatch(mock.cacheStats);
        break;
      }

      case 'refreshCatalogs': {
        // Real host streams `refreshProgress` (shape not finalized) then a fresh `catalog`.
        // The mock just waits a beat and re-sends the same catalogue.
        setTimeout(() => {
          dispatch({ type: 'catalog', clips: Array.from(mock.clipsByKey.values()) });
        }, 1200);
        break;
      }

      case 'startNow':
        // eslint-disable-next-line no-console
        console.log('[shim] startNow: would call NativeMethods.StartConfiguredScreenSaver() on the real host');
        break;

      default:
        // eslint-disable-next-line no-console
        console.warn('[shim] unknown page→host message type', msg.type);
    }
  }

  // ---- bootstrap ------------------------------------------------------------------------

  function loadFixture() {
    fetch(FIXTURE_URL)
      .then((r) => {
        if (!r.ok) throw new Error(`fixture fetch failed: HTTP ${r.status}`);
        return r.json();
      })
      .then((fixture) => {
        for (const message of fixture.messages) {
          const { delayMs, ...rest } = message;
          setTimeout(() => {
            if (rest.type === 'settings') mock.settings = rest.values;
            if (rest.type === 'cacheStats') mock.cacheStats = rest;
            if (rest.type === 'catalog') {
              for (const clip of rest.clips) mock.clipsByKey.set(clip.key, clip);
            }
            if (rest.type === 'stateDelta') {
              for (const [key, state] of Object.entries(rest.clips)) mock.stateByKey.set(key, state);
            }
            dispatch(rest);
          }, delayMs || 0);
        }
      })
      .catch((err) => {
        // eslint-disable-next-line no-console
        console.error('[shim] could not load fixture.json -- serve this folder over HTTP ' +
          '(fetch() of a local file fails under file:// in most browsers). ' +
          'e.g. `npx http-server docs/browser-mock` or `python -m http.server` from that folder.', err);
      });
  }

  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage(obj) {
      // WebView2's real postMessage accepts either a string or a JSON-able value; app.js
      // always sends a plain object, same as PostWebMessageAsJson expects on the way back.
      handlePageMessage(typeof obj === 'string' ? JSON.parse(obj) : obj);
    },
    addEventListener(type, fn) {
      if (type === 'message') listeners.push(fn);
    },
    removeEventListener(type, fn) {
      if (type !== 'message') return;
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    },
  };

  loadFixture();
})();
