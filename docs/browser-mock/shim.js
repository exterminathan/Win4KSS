// docs/browser-mock/shim.js
//
// Dev-only stand-in for window.chrome.webview, so the real production UI
// (src/Win4K.ScreenSaver/Browser/ui/{index.html,app.css,app.js}) can be opened in any
// browser and exercised without WebView2 or a compiled host. Loaded only by
// docs/browser-mock/dev.html -- app.js never references this file and must keep working
// unchanged against the real WebView2 bridge.
//
// What it does, mirroring BrowserBridge.cs (docs/browser-bridge-contract.md):
//   1. Implements chrome.webview.postMessage(obj) (page -> host) and
//      chrome.webview.addEventListener('message', fn) (host -> page).
//   2. Posts nothing until the page sends `ready`; then replays fixture.json (settings,
//      catalog, a full stateDelta, cacheStats). A second `ready` (a reload) replays it again.
//   3. Answers page -> host messages roughly as the host does: requestPreviews from the
//      sample files under assets/, setPinned with a budget check (pinRefused) and a
//      simulated download, setRotation/setAutoAdd/saveSettings with settings + stateDelta
//      echoes, refreshCatalogs with refreshProgress then a catalog, pickFolder via prompt(),
//      and a notice for the things a browser can't do (open a folder, start the saver).
//
// Pure message plumbing: this file knows nothing about the DOM.

(function () {
  'use strict';

  const FIXTURE_URL = 'fixture.json';
  const ASSETS = 'assets/';
  const STILLS = ['still-0.jpg', 'still-1.jpg', 'still-2.jpg', 'still-3.jpg'];
  const HOVERS = ['hover-a.mp4', 'hover-b.mp4'];
  const GiB = 1024 * 1024 * 1024;
  const UNKNOWN_BYTES = 350 * 1024 * 1024; // PinManager's guess for a clip of unknown size

  /** @type {Array<(ev: {data: any}) => void>} */
  const listeners = [];

  const mock = {
    fixture: null,
    settings: null,      // settings message (values + sources + about)
    cacheStats: null,
    clipsByKey: new Map(),
    stateByKey: new Map(),
    previewVersion: 0,
    timers: [],
  };

  function log(direction, type, payload) {
    // eslint-disable-next-line no-console
    console.log(`%c[shim] ${direction} ${type}`, direction === 'page→host' ? 'color:#9cf' : 'color:#9f9', payload);
  }

  function dispatch(message) {
    const copy = JSON.parse(JSON.stringify(message)); // the real bridge posts serialized JSON
    log('host→page', copy.type, copy);
    for (const fn of listeners.slice()) {
      try {
        fn({ data: copy });
      } catch (err) {
        // eslint-disable-next-line no-console
        console.error('[shim] a message listener threw', err);
      }
    }
  }

  const later = (fn, ms) => mock.timers.push(setTimeout(fn, ms));
  const notice = (level, text) => dispatch({ type: 'notice', level, text });

  function postSettings() {
    const ids = mock.settings.values.availableSources;
    const auto = new Set(mock.settings.values.autoAddSources);
    for (const s of mock.settings.sources) {
      s.autoAdd = auto.has(s.id);
      s.clipCount = Array.from(mock.clipsByKey.values()).filter((c) => (c.sources || []).includes(s.id)).length;
    }
    mock.settings.sources.sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id));
    dispatch(Object.assign({ type: 'settings' }, mock.settings));
  }

  function postStats() { dispatch(Object.assign({ type: 'cacheStats' }, mock.cacheStats)); }

  function randomAsset(list, key) {
    // Deterministic per key, so reloading the page doesn't shuffle which sample a card gets.
    let hash = 0;
    for (let i = 0; i < key.length; i++) hash = (hash * 31 + key.charCodeAt(i)) >>> 0;
    return list[hash % list.length];
  }

  function placeholderColorFor(category) {
    // A stable, muted tint per category, like MediaPreviewService.PlaceholderColor.
    const name = (category || 'Other').toLowerCase();
    let hash = 2166136261;
    for (let i = 0; i < name.length; i++) { hash = ((hash ^ name.charCodeAt(i)) * 16777619) >>> 0; }
    const h = (hash % 360) / 360, s = 0.35, l = 0.3;
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
    const ch = (t) => {
      t = ((t % 1) + 1) % 1;
      const v = t < 1 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3 ? p + (q - p) * (2 / 3 - t) * 6 : p;
      return Math.round(Math.max(0, Math.min(1, v)) * 255).toString(16).padStart(2, '0');
    };
    return `#${ch(h + 1 / 3)}${ch(h)}${ch(h - 1 / 3)}`;
  }

  function sendPreviewReady(key, kind) {
    const clip = mock.clipsByKey.get(key);
    const color = placeholderColorFor(clip ? clip.category : null);
    const caps = mock.settings.values.previewCapabilities || {};
    if (kind === 'Hover' && caps.ffmpegAvailable === false) {
      dispatch({ type: 'previewReady', key, kind, status: 'Unavailable', url: null, placeholderColor: color });
      return;
    }
    mock.previewVersion += 1;
    const file = kind === 'Hover' ? randomAsset(HOVERS, key) : randomAsset(STILLS, key);
    dispatch({ type: 'previewReady', key, kind, status: 'Ready', url: `${ASSETS}${file}?v=${mock.previewVersion}`, placeholderColor: color });
  }

  function mergeStateDelta(partial) {
    const full = {};
    for (const [key, patch] of Object.entries(partial)) {
      const next = Object.assign({}, mock.stateByKey.get(key) || {}, patch);
      mock.stateByKey.set(key, next);
      full[key] = next; // the host always sends whole clip states
    }
    if (Object.keys(full).length) dispatch({ type: 'stateDelta', clips: full });
  }

  const estimate = (key) => (mock.clipsByKey.get(key) || {}).sizeBytes || UNKNOWN_BYTES;
  const keysOfSource = (sourceId) => Array.from(mock.clipsByKey.values()).filter((c) => (c.sources || []).includes(sourceId)).map((c) => c.key);

  function simulateDownload(key) {
    const total = estimate(key);
    let fraction = 0;
    const step = () => {
      const st = mock.stateByKey.get(key);
      if (!st || !st.pinned) return;
      fraction = Math.min(1, fraction + 0.08 + Math.random() * 0.1);
      if (fraction >= 1) {
        mergeStateDelta({ [key]: { cached: true, download: null, lastPlayedUtc: new Date().toISOString() } });
        mock.cacheStats.pendingPins = Math.max(0, mock.cacheStats.pendingPins - 1);
        mock.cacheStats.pinnedFiles += 1;
        postStats();
        return;
      }
      mergeStateDelta({ [key]: { download: { state: 'Downloading', fraction, bytesReceived: Math.round(total * fraction), totalBytes: total, error: null } } });
      later(step, 250);
    };
    later(step, 400);
  }

  // ---- page -> host --------------------------------------------------------------------

  function handlePageMessage(msg) {
    log('page→host', msg.type, msg);
    const values = mock.settings && mock.settings.values;
    switch (msg.type) {
      case 'ready':
        replay();
        break;

      case 'setRotation': {
        const patch = {};
        for (const key of msg.keys) patch[key] = { inRotation: !!msg.value };
        mergeStateDelta(patch);
        break;
      }

      case 'setAutoAdd': {
        const current = new Set(values.autoAddSources || []);
        if (msg.value) current.add(msg.sourceId); else current.delete(msg.sourceId);
        values.autoAddSources = Array.from(current).sort();
        postSettings();
        if (msg.includeExisting) {
          const patch = {};
          for (const key of keysOfSource(msg.sourceId)) patch[key] = { inRotation: !!msg.value };
          mergeStateDelta(patch);
        }
        break;
      }

      case 'setPinned': {
        if (!msg.value) {
          const patch = {};
          for (const key of msg.keys) {
            const st = mock.stateByKey.get(key) || {};
            if (st.pinned) {
              mock.cacheStats.pinnedBytes = Math.max(0, mock.cacheStats.pinnedBytes - estimate(key));
              if (st.cached) mock.cacheStats.rollingBytes += estimate(key);
            }
            patch[key] = { pinned: false, download: null };
          }
          mergeStateDelta(patch);
          postStats();
          break;
        }

        // PinManager.CanPin: what is pinned already, plus the new keys, must fit the budget.
        let required = mock.cacheStats.pinnedBytes;
        for (const key of new Set(msg.keys)) if (!(mock.stateByKey.get(key) || {}).pinned) required += estimate(key);
        const budget = mock.cacheStats.budgetBytes;
        if (required > budget) {
          dispatch({
            type: 'pinRefused', keys: msg.keys,
            requiredGb: Math.ceil(required / GiB), budgetGb: values.cacheBudgetGb,
            shortfallGb: Math.ceil(((required - budget) / GiB) * 10) / 10,
            requiredBytes: required, budgetBytes: budget, shortfallBytes: required - budget,
          });
          break;
        }

        const patch = {};
        for (const key of msg.keys) {
          const st = mock.stateByKey.get(key) || {};
          if (st.pinned) continue;
          mock.cacheStats.pinnedBytes += estimate(key);
          if (st.cached) { patch[key] = { pinned: true }; continue; }
          mock.cacheStats.pendingPins += 1;
          patch[key] = { pinned: true, download: { state: 'Queued', fraction: null, bytesReceived: 0, totalBytes: null, error: null } };
          simulateDownload(key);
        }
        mergeStateDelta(patch);
        postStats();
        break;
      }

      case 'selectClip':
      case 'panelRect':
      case 'cancelPreviews':
        break; // no native panel and no preview workers in a plain browser

      case 'requestPreviews': {
        const kinds = msg.kinds === 'Both' ? ['Still', 'Hover'] : [msg.kinds];
        for (const key of msg.keys) {
          for (const kind of kinds) {
            // Stills answer at once; hover clips take a moment, like ffmpeg.
            later(() => sendPreviewReady(key, kind), kind === 'Hover' ? 600 + Math.random() * 600 : 30 + Math.random() * 200);
          }
        }
        break;
      }

      case 'saveSettings': {
        const p = msg.patch || {};
        for (const [k, v] of Object.entries(p)) {
          if (k === 'previewCapabilities' || k === 'availableSources' || !(k in values)) continue;
          if (k === 'cacheBudgetGb') {
            values.cacheBudgetGb = Math.max(1, Number(v));
            mock.cacheStats.budgetBytes = Math.round(values.cacheBudgetGb * GiB);
            postStats();
          } else if (k === 'hotkey') {
            const parts = String(v || '').split('+').filter(Boolean);
            const key = parts.find((x) => !/^(ctrl|control|alt|shift)$/i.test(x));
            if (parts.length && (!key || parts.length < 2)) { notice('warning', `"${v}" is not a usable hotkey: it needs Ctrl, Alt or Shift plus a key.`); continue; }
            values.hotkey = parts.length
              ? ['CTRL', 'ALT', 'SHIFT'].filter((m) => parts.some((x) => x.toUpperCase().startsWith(m.slice(0, 4))))
                .concat([key.replace(/^Key/, '').replace(/^Digit/, '').toUpperCase()]).join('+')
              : null;
          } else {
            values[k] = v;
          }
          if (k === 'cacheDirectory' || k === 'ffmpegPath') notice('info', `The new ${k === 'ffmpegPath' ? 'ffmpeg path' : 'cache folder'} is used the next time the browser starts.`);
        }
        postSettings();
        break;
      }

      case 'pickFolder': {
        // eslint-disable-next-line no-alert
        const folder = window.prompt(`[dev harness] stands in for the native folder dialog (${msg.purpose}):`, 'D:\\Videos\\Aerial');
        if (!folder) break;
        if (msg.purpose === 'localFolder') values.localFolders = (values.localFolders || []).concat([folder]);
        else values.cacheDirectory = folder;
        postSettings();
        break;
      }

      case 'refreshCatalogs': {
        const sources = ['archive-org', 'nasa', 'aerial-manifests'];
        let done = 0;
        const tick = () => {
          done += 1;
          if (done <= 6) {
            const sourceId = sources[done % sources.length];
            dispatch({ type: 'refreshProgress', running: true, sourceId, stage: 'items', done: done * 17, total: 102, ok: null, sources: null, error: null });
            later(tick, 300);
            return;
          }
          dispatch({
            type: 'refreshProgress', running: false, sourceId: null, stage: null, done: null, total: null, ok: true, error: null,
            sources: sources.map((s) => ({ sourceId: s, succeeded: true, assetCount: keysOfSource(s).length, added: 0, removed: 0, error: null })),
          });
          dispatch({ type: 'catalog', clips: Array.from(mock.clipsByKey.values()) });
          const full = {};
          for (const [k, v] of mock.stateByKey) full[k] = v;
          dispatch({ type: 'stateDelta', clips: full });
          postStats();
          postSettings();
        };
        later(tick, 200);
        break;
      }

      case 'purgeCache': {
        const patch = {};
        let n = 0;
        for (const [key, st] of mock.stateByKey) if (st.cached && !st.pinned) { patch[key] = { cached: false, lastPlayedUtc: null }; n++; }
        mergeStateDelta(patch);
        mock.cacheStats.rollingBytes = 0;
        mock.cacheStats.rollingFiles = 0;
        postStats();
        notice('info', `Deleted ${n} cached clip(s). Clips kept offline were not touched.`);
        break;
      }

      case 'retryDownloads': {
        for (const [key, st] of mock.stateByKey) {
          if (st.download && st.download.state === 'Failed') {
            mergeStateDelta({ [key]: { download: { state: 'Queued', fraction: null, bytesReceived: 0, totalBytes: null, error: null } } });
            simulateDownload(key);
          }
        }
        break;
      }

      case 'startNow':
        notice('info', '[dev harness] the real host would start the screen saver now.');
        break;

      case 'openFolder':
        notice('info', `[dev harness] the real host would open the ${msg.which} folder in Explorer.`);
        break;

      case 'log':
        // eslint-disable-next-line no-console
        (msg.level === 'error' ? console.error : console.log)('[shim] page log:', msg.text);
        break;

      default:
        // eslint-disable-next-line no-console
        console.warn('[shim] unknown page→host message type', msg.type);
    }
  }

  // ---- bootstrap ------------------------------------------------------------------------

  function seed(fixture) {
    mock.clipsByKey.clear();
    mock.stateByKey.clear();
    for (const message of fixture.messages) {
      if (message.type === 'settings') mock.settings = JSON.parse(JSON.stringify({ values: message.values, sources: message.sources, about: message.about }));
      if (message.type === 'cacheStats') { const { type, delayMs, ...rest } = message; mock.cacheStats = Object.assign({}, rest); }
      if (message.type === 'catalog') for (const clip of message.clips) mock.clipsByKey.set(clip.key, clip);
      if (message.type === 'stateDelta') for (const [key, st] of Object.entries(message.clips)) mock.stateByKey.set(key, Object.assign({}, st));
    }
  }

  /** Answers `ready`: everything from scratch, as the host does after a (re)load. */
  function replay() {
    for (const t of mock.timers) clearTimeout(t);
    mock.timers = [];
    const go = (fixture) => {
      if (!mock.fixture) { mock.fixture = fixture; seed(fixture); }
      for (const message of fixture.messages) {
        later(() => {
          if (message.type === 'settings') postSettings();
          else if (message.type === 'cacheStats') postStats();
          else if (message.type === 'stateDelta') {
            const full = {};
            for (const [k, v] of mock.stateByKey) full[k] = v;
            dispatch({ type: 'stateDelta', clips: full });
          } else {
            const { delayMs, ...rest } = message;
            dispatch(rest);
          }
        }, message.delayMs || 0);
      }
    };
    if (mock.fixture) { go(mock.fixture); return; }
    fetch(FIXTURE_URL)
      .then((r) => {
        if (!r.ok) throw new Error(`fixture fetch failed: HTTP ${r.status}`);
        return r.json();
      })
      .then(go)
      .catch((err) => {
        // eslint-disable-next-line no-console
        console.error('[shim] could not load fixture.json -- serve the repo over HTTP ' +
          '(e.g. `python -m http.server` from the repo root, then open /docs/browser-mock/dev.html).', err);
      });
  }

  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage(obj) {
      // Async, like the real one: WebMessageReceived fires on the host's next turn.
      const msg = typeof obj === 'string' ? JSON.parse(obj) : JSON.parse(JSON.stringify(obj));
      setTimeout(() => handlePageMessage(msg), 0);
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
})();
