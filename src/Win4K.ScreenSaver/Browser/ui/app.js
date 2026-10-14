// src/Win4K.ScreenSaver/Browser/ui/app.js
//
// The clip browser's entire client-side logic. Vanilla JS, no build step, no framework,
// no CDN -- see "Phase E -- Browser UI (WebView2)" in docs/squishy-teacup-plan.md and the
// wire format in docs/browser-bridge-contract.md. Talks to the host exclusively through
// window.chrome.webview (PostWebMessageAsJson / WebMessageReceived); in production that
// object is injected by WebView2 before this script runs, and in the dev harness
// (docs/browser-mock/dev.html) by docs/browser-mock/shim.js, loaded first. This file
// never references the shim and must not know it exists.
//
// Section map:
//   1. Bridge            -- thin wrapper over window.chrome.webview, + page error logging
//   2. Constants          -- facet order, state-tag strings
//   3. Store              -- all mutable client state, in one place
//   4. Tag / filter model -- combining catalogue tags with live state, faceted counts
//   5. Sorting
//   6. Grid rendering
//   7. Hover preview
//   8. Detail pane + the native preview panel (panelRect)
//   9. Selection / bulk bar
//  10. Sidebar (facets)
//  11. Toolbar (search, sort, view size, refresh, start now)
//  12. Settings panel
//  13. Toasts and the confirm dialog
//  14. Host -> page message handlers
//  15. Init (ends by posting `ready`)

(function () {
  'use strict';

  // ======================================================================================
  // 1. Bridge
  // ======================================================================================

  const Bridge = (function () {
    const handlers = new Map();

    function webview() {
      return window.chrome && window.chrome.webview;
    }

    function dispatch(msg) {
      if (!msg || typeof msg.type !== 'string') return;
      const fn = handlers.get(msg.type);
      if (!fn) return; // unknown types are fine to ignore
      try {
        fn(msg);
      } catch (err) {
        reportError(`handling ${msg.type}: ${err && err.stack ? err.stack : err}`);
      }
    }

    function send(type, payload) {
      const wv = webview();
      if (!wv) {
        // eslint-disable-next-line no-console
        console.warn('[app] no host to send to', type, payload);
        return;
      }
      wv.postMessage(Object.assign({ type }, payload));
    }

    /** Page errors go to win4k.log as "Page: ..." (the contract's `log` message). */
    function reportError(text) {
      // eslint-disable-next-line no-console
      console.error('[app]', text);
      send('log', { level: 'error', text: String(text) });
    }

    return {
      on(type, fn) { handlers.set(type, fn); },
      send,
      reportError,
      log(level, text) { send('log', { level, text: String(text) }); },
      /** Install the listener, then say `ready`: the host posts nothing before it. */
      connect() {
        const wv = webview();
        if (!wv) {
          // eslint-disable-next-line no-console
          console.warn('[app] window.chrome.webview is not present -- running with no host. ' +
            'Open this page through docs/browser-mock/dev.html for a working mock, or inside WebView2.');
          return;
        }
        wv.addEventListener('message', (e) => dispatch(e.data));
        send('ready', {});
      },
    };
  })();

  window.addEventListener('error', (e) => {
    Bridge.reportError(`${e.message} at ${e.filename || '?'}:${e.lineno || 0}:${e.colno || 0}` +
      (e.error && e.error.stack ? `\n${e.error.stack}` : ''));
  });
  window.addEventListener('unhandledrejection', (e) => {
    const r = e.reason;
    Bridge.reportError(`unhandled rejection: ${r && r.stack ? r.stack : r}`);
  });

  // ======================================================================================
  // 2. Constants
  // ======================================================================================

  // TagBuilder.cs enum order -- also the sidebar's group order.
  const TAG_FACETS = [
    'Source', 'Category', 'Subcategory', 'Subject', 'TimeOfDay',
    'Resolution', 'Aspect', 'DynamicRange', 'Duration', 'Codec', 'Licence', 'State',
  ];

  const FACET_LABELS = {
    Source: 'Source', Category: 'Category', Subcategory: 'Subcategory', Subject: 'Subject',
    TimeOfDay: 'Time of day', Resolution: 'Resolution', Aspect: 'Aspect',
    DynamicRange: 'Dynamic range', Duration: 'Duration', Codec: 'Codec', Licence: 'Licence', State: 'State',
  };

  // Facets with long tails show this many values until "Show all" is clicked.
  const FACET_PREVIEW_COUNT = 12;

  // Mirrors TagBuilder.Values -- the live State facet's exact strings.
  const STATE_VALUES = {
    IN_ROTATION: 'In rotation', KEPT_OFFLINE: 'Kept offline', CACHED: 'Cached',
    DOWNLOADING: 'Downloading', NEW: 'New', UNHEALTHY: 'Unhealthy',
  };

  const RESOLUTION_RANK = { '1080p': 1, '4K': 2, '5K+': 3, '8K': 4 };

  // ======================================================================================
  // 3. Store
  // ======================================================================================

  const Store = {
    clips: new Map(),          // key -> catalog clip record
    order: [],                 // keys, catalog arrival order (stable fallback sort)
    state: new Map(),          // key -> stateDelta record (inRotation, pinned, cached, download, health, isNew, lastPlayedUtc)
    preview: new Map(),        // key -> { still: {status,url,color}, hover: {status,url,color} }
    settings: null,            // settings.values, or null until the first `settings` message
    sources: [],               // settings.sources: [{ id, name, clipCount, autoAdd }]
    about: null,               // settings.about
    cacheStats: null,          // cacheStats message, or null until the first one arrives
    catalogLoaded: false,

    updateStatus: null,        // last updateStatus message, or null until the first check completes
    updateCheckedManually: false, // true once "Check now" has been clicked this session
    updateInstallProgress: null,  // last updateInstallProgress message, or null

    visible: [],               // keys passing the current filters, sorted
    filters: { search: '', facets: new Map() }, // facetName -> Set<value>
    collapsedFacets: new Set(),
    expandedFacets: new Set(), // long facets showing every value
    sort: { field: 'addedUtc', dir: 'desc' },
    viewSize: 'm',

    selection: new Set(),
    detailKey: null,
    lastClickedKey: null,

    requestedStill: new Set(), // keys already asked for a Still preview (avoid re-asking every scroll)
  };

  // ======================================================================================
  // 4. Tag / filter model
  // ======================================================================================

  /** Live State-facet tags for one clip, derived from its stateDelta record. Mirrors
   *  TagBuilder.Build's State block -- including trusting `isNew` as given; the page never
   *  re-derives "new" itself (that's TagBuilder.IsNew's job, host-side). */
  function stateTags(state) {
    if (!state) return [];
    const tags = [];
    if (state.inRotation) tags.push({ facet: 'State', value: STATE_VALUES.IN_ROTATION });
    if (state.pinned) tags.push({ facet: 'State', value: STATE_VALUES.KEPT_OFFLINE });
    if (state.cached) tags.push({ facet: 'State', value: STATE_VALUES.CACHED });
    if (state.download && state.download.state === 'Downloading') tags.push({ facet: 'State', value: STATE_VALUES.DOWNLOADING });
    if (state.isNew) tags.push({ facet: 'State', value: STATE_VALUES.NEW });
    if (state.health === 'Unhealthy') tags.push({ facet: 'State', value: STATE_VALUES.UNHEALTHY });
    return tags;
  }

  function combinedTags(key) {
    const clip = Store.clips.get(key);
    if (!clip) return [];
    return (clip.tags || []).concat(stateTags(Store.state.get(key)));
  }

  /** The part of a clip's state the State facet and filters depend on. */
  function stateFlags(state) {
    if (!state) return '';
    return [state.inRotation, state.pinned, state.cached, state.health, state.isNew,
      !!(state.download && state.download.state === 'Downloading')].join('|');
  }

  function searchText(clip) {
    if (clip._search === undefined) {
      clip._search = [clip.title, clip.category, clip.subcategory, clip.creator, clip.description]
        .concat(clip.subjects || [])
        .concat((clip.captions || []).map((c) => c.text))
        .filter(Boolean)
        .join(' \u0000 ')
        .toLowerCase();
    }
    return clip._search;
  }

  function matchesSearch(clip, text) {
    return !text || searchText(clip).includes(text);
  }

  /** True if `key` satisfies every active facet filter except `excludeFacet` (OR within a
   *  facet's selected values, AND across facets). Passing null applies all of them. */
  function matchesFacetsExcept(tags, excludeFacet) {
    for (const [facet, values] of Store.filters.facets) {
      if (facet === excludeFacet || !values || values.size === 0) continue;
      if (!tags.some((t) => t.facet === facet && values.has(t.value))) return false;
    }
    return true;
  }

  function passesAllFilters(key) {
    const clip = Store.clips.get(key);
    return !!clip && matchesSearch(clip, Store.filters.search) && matchesFacetsExcept(combinedTags(key), null);
  }

  /** facet -> Map(value -> count), each counted against clips that pass every *other*
   *  active facet and the search text -- standard faceted-search counting, so picking a
   *  value in one facet narrows the others' counts without ever zeroing out your own. */
  function computeFacetCounts() {
    const result = new Map();
    for (const facet of TAG_FACETS) result.set(facet, new Map());

    for (const key of Store.order) {
      const clip = Store.clips.get(key);
      if (!clip || !matchesSearch(clip, Store.filters.search)) continue;
      const tags = combinedTags(key);
      for (const facet of TAG_FACETS) {
        if (!matchesFacetsExcept(tags, facet)) continue;
        const counts = result.get(facet);
        const here = new Set();
        for (const t of tags) if (t.facet === facet) here.add(t.value);
        for (const value of here) counts.set(value, (counts.get(value) || 0) + 1);
      }
    }
    return result;
  }

  function toggleFacetValue(facet, value, active) {
    let set = Store.filters.facets.get(facet);
    if (!set) { set = new Set(); Store.filters.facets.set(facet, set); }
    if (active) set.add(value); else set.delete(value);
    if (set.size === 0) Store.filters.facets.delete(facet);
    onFiltersChanged();
  }

  function isFacetValueActive(facet, value) {
    const set = Store.filters.facets.get(facet);
    return !!(set && set.has(value));
  }

  function onFiltersChanged() {
    recomputeVisible();
    buildGrid();
    renderFacets();
    refreshDetailTagChips();
  }

  // ======================================================================================
  // 5. Sorting
  // ======================================================================================

  function resolutionRank(clip) {
    if (clip.width && clip.height) return clip.width * clip.height;
    const tag = (clip.tags || []).find((t) => t.facet === 'Resolution');
    return tag ? (RESOLUTION_RANK[tag.value] || null) : null;
  }

  function cmpNullsLast(a, b, dir) {
    const an = a === null || a === undefined || Number.isNaN(a);
    const bn = b === null || b === undefined || Number.isNaN(b);
    if (an && bn) return 0;
    if (an) return 1;
    if (bn) return -1;
    if (a < b) return dir === 'asc' ? -1 : 1;
    if (a > b) return dir === 'asc' ? 1 : -1;
    return 0;
  }

  function sortValue(key, field) {
    const clip = Store.clips.get(key);
    switch (field) {
      case 'title': return (clip.title || '').toLowerCase();
      case 'durationSeconds': return clip.durationSeconds;
      case 'sizeBytes': return clip.sizeBytes;
      case 'resolution': return resolutionRank(clip);
      case 'addedUtc': return clip.addedUtc ? Date.parse(clip.addedUtc) : null;
      case 'source': return sourceDisplayName(clip).toLowerCase();
      case 'lastPlayed': {
        // stateDelta.lastPlayedUtc: from the cache index, so only cached clips have one.
        const s = Store.state.get(key);
        return s && s.lastPlayedUtc ? Date.parse(s.lastPlayedUtc) : null;
      }
      default: return null;
    }
  }

  function recomputeVisible() {
    const { field, dir } = Store.sort;
    const rows = [];
    for (const key of Store.order) if (passesAllFilters(key)) rows.push({ key, v: sortValue(key, field) });
    // Stable (Array.prototype.sort is), ties fall back to title then catalogue order.
    rows.sort((a, b) => cmpNullsLast(a.v, b.v, dir) ||
      (field !== 'title' ? cmpNullsLast(sortValue(a.key, 'title'), sortValue(b.key, 'title'), 'asc') : 0));
    Store.visible = rows.map((r) => r.key);
  }

  // ======================================================================================
  // 6. Grid rendering
  // ======================================================================================

  const cardEls = new Map(); // key -> element, rebuilt alongside Store.visible
  let stillObserver = null;
  let renderedVisible = [];  // the order the grid currently shows

  function esc(s) {
    return String(s === null || s === undefined ? '' : s).replace(/[&<>"']/g, (c) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
  }

  function pad2(n) { return String(n).padStart(2, '0'); }

  function formatDuration(seconds) {
    if (seconds === null || seconds === undefined) return '';
    const s = Math.round(seconds);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    return h > 0 ? `${h}:${pad2(m)}:${pad2(sec)}` : `${m}:${pad2(sec)}`;
  }

  function formatBytes(n) {
    if (n === null || n === undefined) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let v = n, i = 0;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v < 10 && i > 0 ? 1 : 0)} ${units[i]}`;
  }

  function formatWhen(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  }

  function tagValue(clip, facet) {
    const t = (clip.tags || []).find((tag) => tag.facet === facet);
    return t ? t.value : null;
  }

  function sourceDisplayName(clip) {
    return tagValue(clip, 'Source') || sourceName(clip.sourceId);
  }

  /** Display name of a rotation source id (settings.sources), else the id itself. */
  function sourceName(id) {
    const s = Store.sources.find((x) => x.id === id);
    return s ? s.name : (id || '');
  }

  function subLine(clip) {
    const bits = [];
    if (clip.category && clip.category !== 'Other' && clip.category !== 'Mixed') bits.push(clip.category);
    if (clip.subcategory) bits.push(clip.subcategory);
    if (bits.length === 0) {
      const src = tagValue(clip, 'Source');
      if (src) bits.push(src);
    }
    const res = tagValue(clip, 'Resolution');
    if (res) bits.push(res);
    return bits.join(' \u00b7 ');
  }

  function buildCardEl(key) {
    const clip = Store.clips.get(key);
    const el = document.createElement('div');
    el.className = 'card';
    el.dataset.key = key;
    el.setAttribute('role', 'listitem');
    el.tabIndex = 0;

    const badges = [];
    const resTag = tagValue(clip, 'Resolution');
    if (resTag) badges.push(`<span class="badge">${esc(resTag)}</span>`);
    if (clip.hasHdr) badges.push('<span class="badge badge-hdr">HDR</span>');

    el.innerHTML = `
      <div class="thumb">
        <div class="thumb-placeholder">${esc(clip.title)}</div>
        <div class="badges">${badges.join('')}</div>
        <div class="state-icons"></div>
        ${clip.durationSeconds ? `<span class="duration-badge">${esc(formatDuration(clip.durationSeconds))}</span>` : ''}
      </div>
      <div class="meta">
        <div class="title" title="${esc(clip.title)}">${esc(clip.title)}</div>
        <div class="sub">${esc(subLine(clip))}</div>
      </div>`;

    updateCardVisuals(key, el);
    updateCardThumb(key, el);
    return el;
  }

  function downloadPercent(download) {
    return download && download.fraction !== null && download.fraction !== undefined
      ? Math.round(download.fraction * 100) : null;
  }

  /** Dynamic bits only: selection, active, state icons, download ring. Called on every
   *  stateDelta for a changed key and on selection changes -- must stay cheap. */
  function updateCardVisuals(key, elOpt) {
    const el = elOpt || cardEls.get(key);
    if (!el) return;
    const state = Store.state.get(key) || {};

    el.classList.toggle('is-selected', Store.selection.has(key));
    el.classList.toggle('is-active', Store.detailKey === key);

    const icons = [];
    if (state.inRotation) icons.push('<span class="state-icon rotation" title="In rotation">\u25CF</span>');
    if (state.pinned) icons.push(`<span class="state-icon pin" title="${state.cached ? 'Kept offline' : 'Kept offline (not downloaded yet)'}">\u2B07</span>`);
    if (state.health === 'Unhealthy') icons.push('<span class="state-icon unhealthy" title="Failed to play in several sessions">!</span>');
    if (state.isNew) icons.push('<span class="state-icon new" title="New">\u2605</span>');
    const iconsEl = el.querySelector('.state-icons');
    const iconsHtml = icons.join('');
    if (iconsEl && iconsEl.innerHTML !== iconsHtml) iconsEl.innerHTML = iconsHtml;

    const thumb = el.querySelector('.thumb');
    let ring = thumb.querySelector('.progress-ring');
    const dl = state.download;
    if (dl && (dl.state === 'Downloading' || dl.state === 'Queued' || dl.state === 'Waiting')) {
      if (!ring) { ring = document.createElement('div'); ring.className = 'progress-ring'; thumb.appendChild(ring); }
      const pct = downloadPercent(dl) || 0;
      ring.style.setProperty('--pct', String(pct));
      ring.classList.toggle('is-queued', dl.state !== 'Downloading');
      ring.title = dl.state === 'Downloading' ? `Downloading: ${pct}%` : dl.state === 'Queued' ? 'Queued' : 'Waiting for another process';
    } else if (ring) {
      ring.remove();
    }
    el.classList.toggle('is-failed', !!(dl && dl.state === 'Failed'));
  }

  /** Paints a card's still image (or tinted placeholder) from Store.preview. Idempotent. */
  function updateCardThumb(key, elOpt) {
    const el = elOpt || cardEls.get(key);
    if (!el) return;
    const pv = Store.preview.get(key) || {};
    const thumb = el.querySelector('.thumb');
    const color = (pv.still && pv.still.color) || (pv.hover && pv.hover.color);
    if (color) thumb.style.backgroundColor = color;

    let img = thumb.querySelector('.still-img');
    if (pv.still && pv.still.status === 'Ready' && pv.still.url) {
      if (!img) {
        img = document.createElement('img');
        img.className = 'thumb-media still-img';
        img.alt = '';
        img.decoding = 'async';
        thumb.insertBefore(img, thumb.firstChild);
      }
      if (img.getAttribute('src') !== pv.still.url) img.src = pv.still.url;
      img.classList.remove('hidden');
      thumb.querySelector('.thumb-placeholder')?.classList.add('hidden');
    } else if (img) {
      img.classList.add('hidden');
      thumb.querySelector('.thumb-placeholder')?.classList.remove('hidden');
    }
  }

  function sameOrder(a, b) {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  }

  function buildGrid() {
    if (hoverKey !== null) endHover();
    if (stillObserver) stillObserver.disconnect();
    cardEls.clear();

    const frag = document.createDocumentFragment();
    for (const key of Store.visible) {
      const el = buildCardEl(key);
      cardEls.set(key, el);
      frag.appendChild(el);
    }

    const gridEl = document.getElementById('grid');
    gridEl.replaceChildren(frag);
    renderedVisible = Store.visible.slice();
    updateGridStatus();
    observeCards();
  }

  /** Rebuilds only if the visible set or its order changed; otherwise repaints in place. */
  function refreshGrid(changedKeys) {
    if (!sameOrder(renderedVisible, Store.visible)) {
      buildGrid();
      return;
    }
    for (const key of changedKeys) updateCardVisuals(key);
  }

  function updateGridStatus() {
    document.getElementById('grid-empty').classList.toggle('hidden', !Store.catalogLoaded || Store.visible.length > 0);
    document.getElementById('grid-loading').classList.toggle('hidden', Store.catalogLoaded);
    document.getElementById('grid-status').textContent = Store.catalogLoaded
      ? `${Store.visible.length.toLocaleString()} of ${Store.clips.size.toLocaleString()} clips`
      : '';
  }

  function observeCards() {
    if (!stillObserver) return;
    for (const el of cardEls.values()) stillObserver.observe(el);
  }

  // Visible cards ask for stills in batches, not one message per card.
  let stillBatch = [];
  let stillBatchTimer = null;
  function requestStillIfNeeded(key) {
    if (Store.requestedStill.has(key)) return;
    Store.requestedStill.add(key);
    stillBatch.push(key);
    if (!stillBatchTimer) {
      stillBatchTimer = setTimeout(() => {
        stillBatchTimer = null;
        const keys = stillBatch;
        stillBatch = [];
        if (keys.length) Bridge.send('requestPreviews', { keys, kinds: 'Still' });
      }, 30);
    }
  }

  // ======================================================================================
  // 7. Hover preview
  //
  // One shared <video> is moved into whichever card's .thumb is currently hovered, rather
  // than giving every one of up to ~1,500 cards its own <video> element. A hover clip is
  // made on demand (ffmpeg, a few seconds); moving to another card cancels a pending one.
  // ======================================================================================

  let hoverVideoEl = null;
  let hoverKey = null;
  let hoverTimer = null;
  let hoverPendingKey = null; // a Hover request we made that has not been answered yet

  function clearHoverTimer() {
    if (hoverTimer) { clearTimeout(hoverTimer); hoverTimer = null; }
  }

  function cancelPendingHover(exceptKey) {
    const key = hoverPendingKey;
    if (!key || key === exceptKey) return;
    hoverPendingKey = null;
    const pv = Store.preview.get(key);
    // cancelPreviews drops both kinds for a key: never cancel a still that's still coming.
    if (pv && pv.still && pv.still.status !== 'Pending') Bridge.send('cancelPreviews', { keys: [key] });
  }

  function beginHoverIntent(key, cardEl) {
    hoverKey = key;
    cancelPendingHover(key);
    const pv = Store.preview.get(key);
    if (pv && pv.hover && pv.hover.status === 'Ready' && pv.hover.url) {
      showHoverVideo(cardEl, pv.hover.url);
      return;
    }
    if (pv && pv.hover && pv.hover.status === 'Unavailable') return; // no ffmpeg: the still stays
    hoverPendingKey = key;
    cardEl.querySelector('.thumb')?.classList.add('is-loading');
    Bridge.send('requestPreviews', { keys: [key], kinds: 'Hover' });
  }

  function showHoverVideo(cardEl, url) {
    const thumb = cardEl.querySelector('.thumb');
    if (!thumb) return;
    thumb.classList.remove('is-loading');
    if (hoverVideoEl.getAttribute('src') !== url) hoverVideoEl.src = url;
    thumb.insertBefore(hoverVideoEl, thumb.querySelector('.badges'));
    hoverVideoEl.classList.remove('hidden');
    hoverVideoEl.currentTime = 0;
    hoverVideoEl.play().catch(() => { /* autoplay may be blocked; the still stays visible underneath */ });
  }

  function endHover() {
    const key = hoverKey;
    hoverKey = null;
    if (key) cardEls.get(key)?.querySelector('.thumb')?.classList.remove('is-loading');
    hoverVideoEl.pause();
    hoverVideoEl.classList.add('hidden');
    if (hoverVideoEl.parentElement) hoverVideoEl.parentElement.removeChild(hoverVideoEl);
  }

  function wireGridHover(gridEl) {
    hoverVideoEl = document.createElement('video');
    hoverVideoEl.className = 'thumb-media hover-video hidden';
    hoverVideoEl.muted = true;
    hoverVideoEl.loop = true;
    hoverVideoEl.playsInline = true;
    hoverVideoEl.addEventListener('error', () => {
      if (hoverVideoEl.getAttribute('src')) Bridge.log('warn', `hover clip failed to play: ${hoverVideoEl.getAttribute('src')}`);
    });

    gridEl.addEventListener('mouseover', (e) => {
      const cardEl = e.target.closest('.card');
      if (!cardEl || cardEl.dataset.key === hoverKey) return;
      clearHoverTimer();
      if (hoverKey !== null) endHover();
      const key = cardEl.dataset.key;
      hoverTimer = setTimeout(() => beginHoverIntent(key, cardEl), 160);
    });

    gridEl.addEventListener('mouseout', (e) => {
      const cardEl = e.target.closest('.card');
      if (!cardEl) return;
      const related = e.relatedTarget && e.relatedTarget.closest && e.relatedTarget.closest('.card');
      if (related === cardEl) return;
      clearHoverTimer();
      if (hoverKey === cardEl.dataset.key) endHover();
    });
  }

  // ======================================================================================
  // 8. Detail pane + the native preview panel
  //
  // The host lays a native mpv HWND over #detail-preview-rect. It draws above ALL page
  // content, so whenever anything covers that box -- the settings panel, the confirm
  // dialog, a toast, the off-canvas sidebar -- or the box is clipped/scrolled, the page
  // must send panelRect { visible: false }. updatePanel() is the one place that decides.
  // ======================================================================================

  let selectedSentKey = null; // what the host was last told to play (selectClip)

  function openDetail(key) {
    const clip = Store.clips.get(key);
    if (!clip) { closeDetail(); return; }
    Store.detailKey = key;
    document.getElementById('detail').classList.remove('hidden');

    document.getElementById('detail-title').textContent = clip.title;

    const metaBits = [];
    if (clip.durationSeconds) metaBits.push(formatDuration(clip.durationSeconds));
    if (clip.width && clip.height) metaBits.push(`${clip.width}\u00d7${clip.height}`);
    if (clip.sizeBytes) metaBits.push(formatBytes(clip.sizeBytes));
    if (clip.codec) metaBits.push(clip.codec.toUpperCase());
    const src = sourceDisplayName(clip);
    if (src) metaBits.push(src);
    document.getElementById('detail-meta').textContent = metaBits.join('  \u00b7  ');

    document.getElementById('detail-captions').innerHTML = (clip.captions || [])
      .map((c) => `<div class="detail-caption-row"><span class="t">${esc(formatDuration(c.atSeconds))}</span>${esc(c.text)}</div>`)
      .join('');

    const descEl = document.getElementById('detail-description');
    descEl.textContent = clip.description || '';
    descEl.classList.toggle('hidden', !clip.description);

    const creditBits = [];
    if (clip.creator) creditBits.push(`By ${clip.creator}`);
    if (clip.licence) creditBits.push(clip.licence);
    const creditEl = document.getElementById('detail-credit');
    creditEl.textContent = creditBits.join(' \u00b7 ');
    creditEl.classList.toggle('hidden', creditBits.length === 0);

    // PinManager.ClipFor returns null for local clips -- nothing to download, so pinning
    // one is a no-op the host silently ignores. Disable it rather than letting the user
    // discover that by nothing happening.
    document.getElementById('toggle-pin').disabled = !!clip.isLocal;
    renderDetailState(key);

    updateDetailPreview();
    markCardActive(key);
    if (selectedSentKey !== key) {
      selectedSentKey = key;
      Bridge.send('selectClip', { key });
    }
    const pv = Store.preview.get(key);
    if (!pv || !pv.still || pv.still.status !== 'Ready') {
      Store.requestedStill.add(key);
      Bridge.send('requestPreviews', { keys: [key], kinds: 'Still' });
    }
    document.getElementById('detail').scrollTop = 0;
    schedulePanelUpdate();
  }

  function closeDetail() {
    if (Store.detailKey === null && selectedSentKey === null) return;
    Store.detailKey = null;
    document.getElementById('detail').classList.add('hidden');
    if (selectedSentKey !== null) {
      selectedSentKey = null;
      Bridge.send('selectClip', { key: null });
    }
    markCardActive(null);
    schedulePanelUpdate();
  }

  function renderDetailState(key) {
    const state = Store.state.get(key) || {};
    document.getElementById('toggle-rotation').checked = !!state.inRotation;
    document.getElementById('toggle-pin').checked = !!state.pinned;
    renderDetailDownload(state);
    renderDetailTags(key);
  }

  function renderDetailDownload(state) {
    const el = document.getElementById('detail-download');
    const bar = document.getElementById('detail-download-bar');
    const text = document.getElementById('detail-download-text');
    const retry = document.getElementById('detail-download-retry');
    const dl = state.download;
    let message = null;
    let pct = null;
    let failed = false;

    if (dl) {
      pct = downloadPercent(dl);
      const bytes = dl.totalBytes
        ? `${formatBytes(dl.bytesReceived)} of ${formatBytes(dl.totalBytes)}`
        : formatBytes(dl.bytesReceived);
      switch (dl.state) {
        case 'Downloading': message = `Downloading\u2026 ${pct !== null ? pct + '%' : ''} (${bytes})`; break;
        case 'Queued':
          message = Store.cacheStats && Store.cacheStats.ownsPins === false
            ? 'Queued \u2014 the screen saver is downloading kept-offline clips.'
            : 'Queued for download.';
          break;
        case 'Waiting': message = 'Waiting \u2014 another Win4KSS process is downloading this clip.'; break;
        case 'Failed': message = `Download failed${dl.error ? ': ' + dl.error : '.'}`; failed = true; break;
        case 'Done': message = 'Downloaded.'; pct = 100; break;
        case 'Cancelled': message = null; break;
        default: message = dl.state;
      }
    }
    if (message === null && state.pinned) message = state.cached ? 'Kept offline \u2014 downloaded.' : 'Kept offline \u2014 queued.';
    else if (message === null && state.cached) message = 'In the disk cache.';
    if (message !== null && state.lastPlayedUtc && !dl) message += ` Last played ${formatWhen(state.lastPlayedUtc)}.`;
    else if (message === null && state.lastPlayedUtc) message = `Last played ${formatWhen(state.lastPlayedUtc)}.`;

    el.classList.toggle('hidden', message === null);
    el.classList.toggle('is-failed', failed);
    text.textContent = message || '';
    bar.classList.toggle('hidden', !(dl && (dl.state === 'Downloading' || dl.state === 'Done')));
    bar.style.setProperty('--pct', String(pct || 0));
    retry.classList.toggle('hidden', !failed);
  }

  function renderDetailTags(key) {
    const tagsEl = document.getElementById('detail-tags');
    tagsEl.innerHTML = '';
    for (const tag of combinedTags(key)) {
      const chip = document.createElement('button');
      chip.type = 'button';
      let cls = 'tag-chip';
      if (isFacetValueActive(tag.facet, tag.value)) cls += ' is-active';
      if (tag.facet === 'State' && tag.value === STATE_VALUES.UNHEALTHY) cls += ' tag-state-unhealthy';
      if (tag.facet === 'State' && tag.value === STATE_VALUES.NEW) cls += ' tag-state-new';
      chip.className = cls;
      chip.textContent = tag.value;
      chip.dataset.facet = tag.facet;
      chip.title = `Filter by ${FACET_LABELS[tag.facet] || tag.facet}`;
      chip.addEventListener('click', () => toggleFacetValue(tag.facet, tag.value, !isFacetValueActive(tag.facet, tag.value)));
      tagsEl.appendChild(chip);
    }
  }

  function refreshDetailTagChips() {
    if (Store.detailKey === null) return;
    for (const chip of document.getElementById('detail-tags').children) {
      chip.classList.toggle('is-active', isFacetValueActive(chip.dataset.facet, chip.textContent));
    }
  }

  /** The still under the native panel: what shows before mpv paints, or if it can't. */
  function updateDetailPreview() {
    const key = Store.detailKey;
    if (!key) return;
    const pv = Store.preview.get(key) || {};
    const placeholderEl = document.getElementById('detail-preview-placeholder');
    const imgEl = document.getElementById('detail-preview-img');
    const color = (pv.still && pv.still.color) || (pv.hover && pv.hover.color);
    placeholderEl.style.backgroundColor = color || '';

    if (pv.still && pv.still.status === 'Ready' && pv.still.url) {
      if (imgEl.getAttribute('src') !== pv.still.url) imgEl.src = pv.still.url;
      imgEl.classList.remove('hidden');
    } else {
      imgEl.classList.add('hidden');
    }
  }

  let lastActiveCardKey = null;
  function markCardActive(key) {
    if (lastActiveCardKey) cardEls.get(lastActiveCardKey)?.classList.remove('is-active');
    if (key) cardEls.get(key)?.classList.add('is-active');
    lastActiveCardKey = key;
  }

  function intersects(a, b) {
    return a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
  }

  /** Anything currently drawn over the video box? (The native panel would hide it.) */
  function panelOccluded(rect) {
    if (document.querySelector('.overlay:not(.hidden)')) return true; // settings, confirm
    for (const el of document.querySelectorAll('.toast, .bulk-bar:not(.hidden), .sidebar.is-open')) {
      if (intersects(rect, el.getBoundingClientRect())) return true;
    }
    return false;
  }

  let lastPanelJson = null;
  function updatePanel() {
    const detailEl = document.getElementById('detail');
    let msg = { x: 0, y: 0, width: 0, height: 0, visible: false };
    if (Store.detailKey !== null && !detailEl.classList.contains('hidden') && !document.hidden) {
      const rect = document.getElementById('detail-preview-rect').getBoundingClientRect();
      const pane = detailEl.getBoundingClientRect();
      const fits = rect.width > 0 && rect.height > 0 &&
        rect.top >= pane.top - 0.5 && rect.bottom <= pane.bottom + 0.5 &&
        rect.left >= pane.left - 0.5 && rect.right <= pane.right + 0.5 &&
        rect.bottom <= window.innerHeight + 0.5 && rect.right <= window.innerWidth + 0.5;
      msg = {
        x: Math.round(rect.left), y: Math.round(rect.top),
        width: Math.round(rect.width), height: Math.round(rect.height),
        visible: fits && !panelOccluded(rect),
      };
    }
    const json = JSON.stringify(msg);
    if (json === lastPanelJson) return;
    lastPanelJson = json;
    Bridge.send('panelRect', msg);
  }

  let panelFrame = 0;
  /** Coalesces layout-changing events into one panelRect per frame. */
  function schedulePanelUpdate() {
    if (panelFrame) return;
    panelFrame = requestAnimationFrame(() => { panelFrame = 0; updatePanel(); });
  }

  // ======================================================================================
  // 9. Selection / bulk bar
  // ======================================================================================

  function handleCardClick(key, e) {
    if (e.shiftKey && Store.lastClickedKey) {
      const order = Store.visible;
      const a = order.indexOf(Store.lastClickedKey);
      const b = order.indexOf(key);
      if (a >= 0 && b >= 0) {
        const [lo, hi] = a < b ? [a, b] : [b, a];
        Store.selection = new Set(order.slice(lo, hi + 1));
      } else {
        Store.selection = new Set([key]);
      }
    } else if (e.ctrlKey || e.metaKey) {
      if (Store.selection.has(key)) Store.selection.delete(key); else Store.selection.add(key);
      Store.lastClickedKey = key;
    } else {
      Store.selection = new Set([key]);
      Store.lastClickedKey = key;
    }
    applySelectionChanged();
  }

  function applySelectionChanged() {
    for (const [key, el] of cardEls) el.classList.toggle('is-selected', Store.selection.has(key));

    const keys = Array.from(Store.selection);
    const bulkBarEl = document.getElementById('bulk-bar');
    if (keys.length === 1) {
      bulkBarEl.classList.add('hidden');
      if (Store.detailKey !== keys[0]) openDetail(keys[0]);
    } else {
      closeDetail();
      bulkBarEl.classList.toggle('hidden', keys.length === 0);
      if (keys.length > 0) document.getElementById('bulk-count').textContent = `${keys.length.toLocaleString()} selected`;
    }
    schedulePanelUpdate();
  }

  function clearSelection() {
    Store.selection.clear();
    Store.lastClickedKey = null;
    applySelectionChanged();
  }

  function wireBulkBar() {
    document.getElementById('bulk-rotation-on').addEventListener('click', () =>
      Bridge.send('setRotation', { keys: Array.from(Store.selection), value: true }));
    document.getElementById('bulk-rotation-off').addEventListener('click', () =>
      Bridge.send('setRotation', { keys: Array.from(Store.selection), value: false }));
    document.getElementById('bulk-pin-on').addEventListener('click', () => requestPin(Array.from(Store.selection), true));
    document.getElementById('bulk-pin-off').addEventListener('click', () => requestPin(Array.from(Store.selection), false));
    document.getElementById('bulk-clear').addEventListener('click', clearSelection);
  }

  function requestPin(keys, value) {
    // No optimistic UI update: pinning can be refused (pinRefused), so the card/detail pin
    // state only ever changes in response to a real stateDelta from the host.
    Bridge.send('setPinned', { keys, value });
  }

  // ======================================================================================
  // 10. Sidebar (facets)
  // ======================================================================================

  function renderFacets() {
    const counts = computeFacetCounts();
    const listEl = document.getElementById('facet-list');
    const scroll = listEl.parentElement.scrollTop;
    listEl.innerHTML = '';

    for (const facet of TAG_FACETS) {
      const valuesMap = counts.get(facet);
      const activeSet = Store.filters.facets.get(facet);
      // A value that is filtered on stays listed even when nothing else matches (count 0).
      if (activeSet) for (const v of activeSet) if (!valuesMap.has(v)) valuesMap.set(v, 0);
      if (!valuesMap || valuesMap.size === 0) continue;

      const group = document.createElement('div');
      group.className = 'facet-group' + (Store.collapsedFacets.has(facet) ? ' is-collapsed' : '');
      group.dataset.facet = facet;

      const head = document.createElement('button');
      head.type = 'button';
      head.className = 'facet-group-head';
      head.innerHTML = `<span>${esc(FACET_LABELS[facet] || facet)}</span>` +
        (activeSet ? `<span class="facet-active-count">${activeSet.size}</span>` : '') +
        '<span class="chevron">\u25BC</span>';
      head.addEventListener('click', () => {
        group.classList.toggle('is-collapsed');
        if (group.classList.contains('is-collapsed')) Store.collapsedFacets.add(facet);
        else Store.collapsedFacets.delete(facet);
      });
      group.appendChild(head);

      const list = document.createElement('div');
      list.className = 'facet-values';
      const sorted = Array.from(valuesMap.entries()).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
      const expanded = Store.expandedFacets.has(facet);
      let shown = 0;

      for (const [value, count] of sorted) {
        const active = !!(activeSet && activeSet.has(value));
        if (!expanded && shown >= FACET_PREVIEW_COUNT && !active) continue;
        shown++;
        const row = document.createElement('label');
        row.className = 'facet-row' + (active ? ' is-active' : '') + (count === 0 ? ' is-zero' : '');
        row.innerHTML = `<input type="checkbox" ${active ? 'checked' : ''}>` +
          `<span class="facet-value-name" title="${esc(value)}">${esc(value)}</span><span class="facet-value-count">${count.toLocaleString()}</span>`;
        row.querySelector('input').addEventListener('change', (e) => toggleFacetValue(facet, value, e.target.checked));
        list.appendChild(row);
      }

      if (sorted.length > FACET_PREVIEW_COUNT) {
        const more = document.createElement('button');
        more.type = 'button';
        more.className = 'link-btn facet-more';
        more.textContent = expanded ? 'Show fewer' : `Show all ${sorted.length.toLocaleString()}`;
        more.addEventListener('click', () => {
          if (expanded) Store.expandedFacets.delete(facet); else Store.expandedFacets.add(facet);
          renderFacets();
        });
        list.appendChild(more);
      }

      group.appendChild(list);
      listEl.appendChild(group);
    }
    listEl.parentElement.scrollTop = scroll;
  }

  function wireSidebar() {
    document.getElementById('clear-filters').addEventListener('click', () => {
      Store.filters.facets.clear();
      Store.filters.search = '';
      document.getElementById('search-input').value = '';
      onFiltersChanged();
    });
    document.getElementById('toggle-sidebar').addEventListener('click', () => {
      document.getElementById('sidebar').classList.toggle('is-open');
      schedulePanelUpdate();
    });
  }

  // ======================================================================================
  // 11. Toolbar
  // ======================================================================================

  function wireToolbar() {
    let searchTimer = null;
    document.getElementById('search-input').addEventListener('input', (e) => {
      clearTimeout(searchTimer);
      const value = e.target.value.trim().toLowerCase();
      searchTimer = setTimeout(() => { Store.filters.search = value; onFiltersChanged(); }, 150);
    });

    document.getElementById('sort-select').addEventListener('change', (e) => {
      const [field, dir] = e.target.value.split(':');
      Store.sort = { field, dir };
      recomputeVisible();
      buildGrid();
    });

    for (const btn of document.querySelectorAll('.viewsize-btn')) {
      btn.addEventListener('click', () => {
        for (const b of document.querySelectorAll('.viewsize-btn')) b.classList.toggle('is-active', b === btn);
        Store.viewSize = btn.dataset.size;
        document.getElementById('grid').className = 'grid grid-' + Store.viewSize;
      });
    }

    document.getElementById('select-all-filtered').addEventListener('click', () => {
      Store.selection = new Set(Store.visible);
      Store.lastClickedKey = null;
      applySelectionChanged();
    });

    document.getElementById('refresh-catalogs').addEventListener('click', () => {
      Bridge.send('refreshCatalogs', {});
      setRefreshing(true, 'Refreshing catalogues\u2026');
    });

    document.getElementById('start-now').addEventListener('click', () => Bridge.send('startNow', {}));
    document.getElementById('open-settings').addEventListener('click', openSettings);
  }

  function setRefreshing(on, text) {
    document.getElementById('refresh-spinner').classList.toggle('hidden', !on);
    document.getElementById('refresh-catalogs').disabled = on;
    const status = document.getElementById('refresh-status');
    status.textContent = on ? (text || '') : '';
    status.classList.toggle('hidden', !on || !text);
  }

  // ======================================================================================
  // 12. Settings panel
  // ======================================================================================

  let settingsBuilt = false;

  function openSettings() {
    document.getElementById('settings-overlay').classList.remove('hidden');
    if (!settingsBuilt && Store.settings) buildSettingsPanes();
    if (settingsBuilt) syncSettingsValues();
    updatePanel(); // hide the native panel before the overlay paints under it
  }

  function closeSettings() {
    document.getElementById('settings-overlay').classList.add('hidden');
    schedulePanelUpdate();
  }

  function showSettingsTab(name) {
    for (const t of document.querySelectorAll('.settings-tab')) t.classList.toggle('is-active', t.dataset.tab === name);
    for (const p of document.querySelectorAll('.settings-pane')) p.classList.toggle('is-active', p.dataset.tab === name);
  }

  function wireSettingsStatic() {
    document.getElementById('settings-close').addEventListener('click', closeSettings);
    document.getElementById('settings-overlay').addEventListener('click', (e) => {
      if (e.target.id === 'settings-overlay') closeSettings();
    });
    for (const tab of document.querySelectorAll('.settings-tab')) {
      tab.addEventListener('click', () => showSettingsTab(tab.dataset.tab));
    }
  }

  // Debounced settings writes: callers patch individual fields; the patch is merged and
  // flushed once typing/dragging settles, so a slider doesn't spam saveSettings.
  let pendingPatch = {};
  let saveTimer = null;
  function patchSettings(patch, immediate) {
    Object.assign(pendingPatch, patch);
    Object.assign(Store.settings, patch); // optimistic locally; the host echoes `settings` back anyway
    clearTimeout(saveTimer);
    const flush = () => {
      saveTimer = null;
      const p = pendingPatch;
      pendingPatch = {};
      Bridge.send('saveSettings', { patch: p });
    };
    if (immediate) flush(); else saveTimer = setTimeout(flush, 400);
  }

  function buildSettingsPanes() {
    settingsBuilt = true;
    buildSourcesPane();
    buildPlaybackPane();
    buildCachePane();
    buildTriggerPane();
    buildAboutPane();
    syncSettingsValues();
  }

  function pane(tab) { return document.querySelector(`.settings-pane[data-tab="${tab}"]`); }

  function switchRow(id, label) {
    return `<label class="switch-row"><span class="switch"><input id="${id}" type="checkbox"><span class="switch-track"></span></span><span class="switch-label">${label}</span></label>`;
  }

  function buildSourcesPane() {
    const p = pane('sources');
    p.innerHTML = `
      <h3>Rotation by source</h3>
      <p class="settings-hint">"Auto-add" puts clips a catalogue refresh finds from that source straight into the rotation. Which existing clips play is up to you: tick them in the grid, or answer the prompt when you flip a switch.</p>
      <div id="source-toggle-list" class="source-toggle-list"></div>

      <h3>Local folders</h3>
      <p class="settings-hint">Folders of video files on this machine. Local clips play from disk, so they are never cached or kept offline.</p>
      <ul id="folder-list" class="folder-list"></ul>
      <div class="settings-row">
        <button id="folder-browse" type="button" class="btn">Add folder\u2026</button>
        <input id="folder-input" type="text" placeholder="or type a path, e.g. D:\\Videos\\Aerial" style="flex:1 1 auto;">
        <button id="folder-add" type="button" class="btn">Add</button>
      </div>
    `;

    document.getElementById('folder-browse').addEventListener('click', () => Bridge.send('pickFolder', { purpose: 'localFolder' }));
    const addTyped = () => {
      const input = document.getElementById('folder-input');
      const value = input.value.trim();
      if (!value) return;
      const folders = (Store.settings.localFolders || []).slice();
      if (!folders.some((f) => f.toLowerCase() === value.toLowerCase())) folders.push(value);
      input.value = '';
      patchSettings({ localFolders: folders }, true);
      renderFolderList();
    };
    document.getElementById('folder-add').addEventListener('click', addTyped);
    document.getElementById('folder-input').addEventListener('keydown', (e) => { if (e.key === 'Enter') addTyped(); });
  }

  function renderFolderList() {
    const listEl = document.getElementById('folder-list');
    if (!listEl) return;
    const folders = Store.settings.localFolders || [];
    listEl.innerHTML = folders.length
      ? folders.map((f, i) => `<li><span>${esc(f)}</span><button type="button" class="remove-folder" data-i="${i}">Remove</button></li>`).join('')
      : '<li class="folder-empty">No local folders.</li>';
    for (const btn of listEl.querySelectorAll('.remove-folder')) {
      btn.addEventListener('click', () => {
        const i = Number(btn.dataset.i);
        patchSettings({ localFolders: folders.slice(0, i).concat(folders.slice(i + 1)) }, true);
        renderFolderList();
      });
    }
  }

  /** Clips of a rotation source (clip.sources) whose inRotation is `inRotation`. */
  function sourceClipCount(sourceId, inRotation) {
    let count = 0;
    for (const [key, clip] of Store.clips) {
      if (!(clip.sources || []).includes(sourceId)) continue;
      if (!!(Store.state.get(key) || {}).inRotation === inRotation) count++;
    }
    return count;
  }

  function renderSourceToggles() {
    const listEl = document.getElementById('source-toggle-list');
    if (!listEl) return;
    const rows = Store.sources.length
      ? Store.sources
      : (Store.settings.availableSources || []).map((id) => ({ id, name: id, clipCount: 0, autoAdd: (Store.settings.autoAddSources || []).includes(id) }));

    listEl.innerHTML = rows.map((s) => {
      const inRotation = sourceClipCount(s.id, true);
      return `
      <div class="source-toggle-row">
        <div class="source-toggle-text">
          <span class="source-name">${esc(s.name)}</span>
          <span class="source-counts">${s.clipCount.toLocaleString()} clip${s.clipCount === 1 ? '' : 's'} \u00b7 ${inRotation.toLocaleString()} in rotation</span>
        </div>
        <label class="switch-row" title="Auto-add new clips from ${esc(s.name)}">
          <span class="switch-label switch-label-dim">Auto-add</span>
          <span class="switch"><input type="checkbox" data-source="${esc(s.id)}" ${s.autoAdd ? 'checked' : ''}><span class="switch-track"></span></span>
        </label>
      </div>`;
    }).join('');

    for (const input of listEl.querySelectorAll('input[type="checkbox"]')) {
      input.addEventListener('change', async () => {
        const sourceId = input.dataset.source;
        const value = input.checked;
        const name = sourceName(sourceId);
        // On: the clips not yet in rotation could join. Off: the ones in it could leave.
        const n = sourceClipCount(sourceId, !value);
        let includeExisting = false;
        if (n > 0) {
          const answer = await confirmDialog(
            value
              ? `New ${name} clips will join the rotation. Also add the existing ${n.toLocaleString()} ${name} clip${n === 1 ? '' : 's'} not in it yet?`
              : `New ${name} clips will no longer join the rotation. Also take the ${n.toLocaleString()} ${name} clip${n === 1 ? '' : 's'} in it out?`,
            [
              { label: value ? `Add ${n.toLocaleString()}` : `Remove ${n.toLocaleString()}`, value: 'yes', accent: true },
              { label: 'Only future clips', value: 'no' },
              { label: 'Cancel', value: 'cancel' },
            ]);
          if (answer === 'cancel' || answer === null) { input.checked = !value; return; }
          includeExisting = answer === 'yes';
        }
        Bridge.send('setAutoAdd', { sourceId, value, includeExisting });
      });
    }
  }

  function buildPlaybackPane() {
    const p = pane('playback');
    p.innerHTML = `
      <h3>Quality</h3>
      <select id="set-quality">
        <option value="Auto">Automatic (match this display)</option>
        <option value="Uhd4KSdr">4K SDR \u2014 about 350 MB per clip</option>
        <option value="Uhd4KHdr">4K HDR \u2014 about 350 MB per clip</option>
        <option value="Hd1080Sdr">1080p SDR</option>
        <option value="Hd1080Hdr">1080p HDR</option>
        <option value="Hd1080H264">1080p H.264 (most compatible)</option>
      </select>
      <p class="settings-hint">Applies to Apple's clips; the others come in one rendition. HDR is PQ-encoded and gets tone-mapped on an SDR display, so SDR is the safer default.</p>

      <h3>Multiple displays</h3>
      <select id="set-multimonitor">
        <option value="MainOnly">Video on the main display only</option>
        <option value="SameOnEach">Same video on every display</option>
        <option value="DifferentVideos">A different video on each display</option>
        <option value="SpanAll">One video spanning all displays</option>
      </select>

      <h3>Order</h3>
      ${switchRow('set-shuffle', 'Shuffle the play order')}
      ${switchRow('set-timeofday', 'Prefer clips matching the time of day')}
      <p class="settings-hint">Only a handful of clips carry a day/night label, so this nudges very little.</p>
    `;
    document.getElementById('set-quality').addEventListener('change', (e) => patchSettings({ quality: e.target.value }, true));
    document.getElementById('set-multimonitor').addEventListener('change', (e) => patchSettings({ multiMonitorMode: e.target.value }, true));
    document.getElementById('set-shuffle').addEventListener('change', (e) => patchSettings({ shuffle: e.target.checked }, true));
    document.getElementById('set-timeofday').addEventListener('change', (e) => patchSettings({ useTimeOfDay: e.target.checked }, true));
  }

  function buildCachePane() {
    const p = pane('cache');
    p.innerHTML = `
      <h3>Disk cache</h3>
      ${switchRow('set-cacheenabled', 'Keep played clips on disk')}

      <div class="settings-row">
        <span class="field-label">Folder</span>
        <input id="set-cachedir" type="text" style="flex:1 1 auto;">
        <button id="cachedir-browse" type="button" class="btn">Browse\u2026</button>
        <button id="cachedir-open" type="button" class="btn">Open</button>
      </div>
      <div class="settings-row">
        <span class="field-label">Limit</span>
        <input id="set-budget" type="range" min="1" max="500" step="1" style="flex:1 1 auto;">
        <span id="set-budget-readout" class="budget-readout"></span>
      </div>
      <p class="settings-hint">Clips kept offline count against the limit and are never evicted; past it, the least recently played of the rest go first.</p>

      <div class="cache-bar" id="cache-bar"></div>
      <div class="cache-legend">
        <span><span class="dot pinned"></span>Kept offline</span>
        <span><span class="dot rolling"></span>Rolling cache</span>
        <span id="cache-legend-free"></span>
      </div>
      <p id="cache-pins" class="settings-hint"></p>
      <div class="settings-row">
        <button id="cache-retry" type="button" class="btn">Retry failed downloads</button>
        <button id="cache-purge" type="button" class="btn btn-danger">Purge rolling cache\u2026</button>
      </div>

      <h3>Previews</h3>
      <div class="settings-row">
        <span class="field-label">ffmpeg</span>
        <input id="set-ffmpeg" type="text" placeholder="found automatically (PATH, C:\\ffmpeg)" style="flex:1 1 auto;">
        <button id="previews-open" type="button" class="btn">Open previews folder</button>
      </div>
      <p id="preview-caps" class="settings-hint"></p>
    `;
    document.getElementById('set-cacheenabled').addEventListener('change', (e) => patchSettings({ cacheEnabled: e.target.checked }, true));
    document.getElementById('set-cachedir').addEventListener('change', (e) => patchSettings({ cacheDirectory: e.target.value }, true));
    document.getElementById('cachedir-browse').addEventListener('click', () => Bridge.send('pickFolder', { purpose: 'cacheDirectory' }));
    document.getElementById('cachedir-open').addEventListener('click', () => Bridge.send('openFolder', { which: 'cache' }));
    document.getElementById('set-ffmpeg').addEventListener('change', (e) => patchSettings({ ffmpegPath: e.target.value.trim() || null }, true));
    document.getElementById('previews-open').addEventListener('click', () => Bridge.send('openFolder', { which: 'previews' }));
    document.getElementById('set-budget').addEventListener('input', (e) => {
      document.getElementById('set-budget-readout').textContent = `${e.target.value} GB`;
      patchSettings({ cacheBudgetGb: Number(e.target.value) });
    });
    document.getElementById('cache-retry').addEventListener('click', () => Bridge.send('retryDownloads', {}));
    document.getElementById('cache-purge').addEventListener('click', async () => {
      const answer = await confirmDialog(
        'Delete every cached clip that is not kept offline? They download again the next time they play.',
        [{ label: 'Purge', value: 'yes', danger: true }, { label: 'Cancel', value: 'no' }]);
      if (answer === 'yes') Bridge.send('purgeCache', {});
    });
  }

  function buildTriggerPane() {
    const p = pane('trigger');
    p.innerHTML = `
      <h3>Start the screen saver on demand</h3>
      <p class="settings-hint">For when you're walking away and don't want to wait out the idle timer. It honours "On resume, display logon screen".</p>
      <div class="settings-row"><button id="set-startnow" type="button" class="btn btn-accent">Start now</button></div>

      <h3>Shortcuts</h3>
      ${switchRow('set-shortcut-desktop', 'Desktop shortcut')}
      ${switchRow('set-shortcut-startmenu', 'Start menu shortcut')}
      <div class="settings-row">
        <span class="field-label">Hotkey</span>
        <input id="set-hotkey" type="text" readonly placeholder="click, then press a key combination" class="hotkey-input">
        <button id="hotkey-clear" type="button" class="btn">Clear</button>
      </div>
      <p class="settings-hint">Windows attaches the hotkey to a shortcut, so it needs one of them. Use Ctrl, Alt or Shift plus a key. Backspace clears it.</p>
    `;
    document.getElementById('set-startnow').addEventListener('click', () => Bridge.send('startNow', {}));
    document.getElementById('set-shortcut-desktop').addEventListener('change', (e) => patchSettings({ shortcutDesktop: e.target.checked }, true));
    document.getElementById('set-shortcut-startmenu').addEventListener('change', (e) => patchSettings({ shortcutStartMenu: e.target.checked }, true));
    document.getElementById('hotkey-clear').addEventListener('click', () => patchSettings({ hotkey: '' }, true));

    const hk = document.getElementById('set-hotkey');
    hk.addEventListener('focus', () => hk.classList.add('is-recording'));
    hk.addEventListener('blur', () => { hk.classList.remove('is-recording'); hk.value = formatHotkey(Store.settings.hotkey); });
    hk.addEventListener('keydown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === 'Escape' || e.key === 'Tab') { hk.blur(); return; }
      if (['Control', 'Alt', 'Shift', 'Meta'].includes(e.key)) {
        hk.value = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift'].filter(Boolean).join('+') + '+\u2026';
        return;
      }
      if ((e.key === 'Backspace' || e.key === 'Delete') && !e.ctrlKey && !e.altKey && !e.shiftKey) {
        patchSettings({ hotkey: '' }, true);
        hk.blur();
        return;
      }
      // DOM KeyboardEvent.code names ("KeyS", "Digit1", "F5"); the host normalises to CTRL+ALT+S
      // and refuses (with a notice) anything without a modifier.
      const combo = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.code].filter(Boolean).join('+');
      patchSettings({ hotkey: combo }, true);
      hk.value = formatHotkey(combo.replace(/\+(?:Key|Digit)(\w)$/, '+$1'));
      hk.blur();
    });
  }

  // TODO(review): section placement/wording is this implementation's own call -- "Updates"
  // sits right after the version line (it's literally about the version shown there) and
  // before "Files"/"Backup"; the button labels ("Check now", "Install update") and the
  // "nothing shown when up to date" behaviour (see renderUpdateStatus) are judgment calls too.
  function buildAboutPane() {
    const p = pane('about');
    p.innerHTML = `
      <h3>Win4KSS</h3>
      <div class="about-block">
        4K video screen saver for Windows.<br>
        Version <code id="about-version"></code> \u00b7 libmpv API <code id="about-mpv"></code>
      </div>
      <h3>Updates</h3>
      ${switchRow('set-autoupdate', 'Check for updates automatically')}
      <div id="update-status" class="update-status"></div>
      <div id="update-progress" class="update-progress"></div>
      <div class="settings-row">
        <button id="update-check-now" type="button" class="btn">Check now</button>
        <button id="update-install" type="button" class="btn btn-accent hidden">Install update</button>
      </div>
      <h3>Files</h3>
      <table class="about-paths">
        <tr><th>Data folder</th><td><code id="about-data"></code></td><td><button type="button" class="btn" data-open="logs">Open</button></td></tr>
        <tr><th>Settings</th><td><code id="about-settings"></code></td><td></td></tr>
        <tr><th>Log</th><td><code id="about-log"></code></td><td></td></tr>
        <tr><th>mpv log</th><td><code id="about-mpvlog"></code></td><td></td></tr>
        <tr><th>Cache</th><td><code id="about-cache"></code></td><td><button type="button" class="btn" data-open="cache">Open</button></td></tr>
        <tr><th>Previews</th><td><code id="about-previews"></code></td><td><button type="button" class="btn" data-open="previews">Open</button></td></tr>
      </table>
      <h3>Backup / move to another machine</h3>
      <p class="settings-hint">
        Export your choices to one readable file: playback and cache settings, shortcuts, the
        rotation and the clips kept offline (each listed by name). Import it on another machine and
        the catalogue rebuilds itself there. Videos and catalogues are not included. Importing
        asks first, and keeps your current settings as settings.before-import.json.
      </p>
      <div class="settings-row">
        <button id="settings-export" type="button" class="btn">Export settings…</button>
        <button id="settings-import" type="button" class="btn">Import settings…</button>
      </div>
      <p class="settings-hint">
        Apple's aerial CDN presents a certificate chaining to Apple Root CA, which Windows does
        not trust out of the box. Rather than disabling verification, Win4KSS pins Apple's real
        root for its own downloads and hands mpv and ffmpeg a certificate bundle of every
        Windows-trusted root plus Apple's.
      </p>
    `;
    for (const btn of p.querySelectorAll('[data-open]')) {
      btn.addEventListener('click', () => Bridge.send('openFolder', { which: btn.dataset.open }));
    }
    document.getElementById('settings-export').addEventListener('click', () => Bridge.send('exportSettings', {}));
    document.getElementById('settings-import').addEventListener('click', () => Bridge.send('importSettings', {}));

    document.getElementById('set-autoupdate').addEventListener('change', (e) => patchSettings({ autoCheckForUpdates: e.target.checked }, true));
    document.getElementById('update-check-now').addEventListener('click', () => {
      Store.updateCheckedManually = true;
      document.getElementById('update-check-now').disabled = true;
      Bridge.send('checkUpdates', {});
    });
    document.getElementById('update-install').addEventListener('click', async () => {
      const s = Store.updateStatus;
      if (!s || !s.available) return;
      const answer = await confirmDialog(
        `Install Win4KSS ${esc(s.latest)}? It downloads and verifies the installer, then runs it — ` +
        'its own prompt (including the Windows UAC prompt) follows from there.',
        [{ label: 'Install', value: 'yes', accent: true }, { label: 'Cancel', value: 'no' }]);
      if (answer !== 'yes') return;
      document.getElementById('update-install').disabled = true;
      Bridge.send('installUpdate', {});
    });

    renderUpdateStatus();
    renderInstallProgress();
  }

  /** The persistent About-tab banner: nothing shown when up to date, unless the user just clicked "Check now". */
  function renderUpdateStatus() {
    const box = document.getElementById('update-status');
    if (!box) return;
    const installBtn = document.getElementById('update-install');
    const checkBtn = document.getElementById('update-check-now');
    const s = Store.updateStatus;

    if (checkBtn) checkBtn.disabled = false;

    if (!s) {
      box.innerHTML = '';
      if (installBtn) installBtn.classList.add('hidden');
      return;
    }

    if (s.available) {
      box.innerHTML =
        `<div class="update-available"><strong>Win4KSS ${esc(s.latest)}</strong> is available (you have ${esc(s.current)}).` +
        (s.notes ? `<div class="update-notes">${esc(s.notes)}</div>` : '') + '</div>';
      if (installBtn) installBtn.classList.remove('hidden');
      return;
    }

    if (installBtn) installBtn.classList.add('hidden');
    if (!Store.updateCheckedManually) {
      box.innerHTML = ''; // a background check found nothing new -- say nothing
      return;
    }
    box.innerHTML = s.error
      ? `<div class="update-error">Could not check for updates: ${esc(s.error)}</div>`
      : `<div class="update-ok">You're up to date (Win4KSS ${esc(s.current)}).</div>`;
  }

  /** Download/verify/launch progress for an install in flight (updateInstallProgress). */
  function renderInstallProgress() {
    const el = document.getElementById('update-progress');
    if (!el) return;
    const p = Store.updateInstallProgress;
    const installBtn = document.getElementById('update-install');

    if (!p) { el.innerHTML = ''; return; }

    if (p.stage === 'Failed') {
      el.innerHTML = `<div class="update-error">Install failed: ${esc(p.error || 'unknown error')}</div>`;
      if (installBtn) installBtn.disabled = false;
      return;
    }

    if (p.stage === 'Downloading') {
      const pct = p.fraction == null ? null : Math.round(p.fraction * 100);
      el.innerHTML = pct == null
        ? '<div class="progress-label">Downloading…</div>'
        : `<div class="progress-bar"><div class="progress-fill" style="width:${pct}%"></div></div><div class="progress-label">Downloading… ${pct}%</div>`;
      return;
    }

    if (p.stage === 'Done') {
      el.innerHTML = '<div class="update-ok">Installer launched — Win4KSS will close now.</div>';
      return;
    }

    // Verifying, Launching, or any future stage: just name it.
    el.innerHTML = `<div class="progress-label">${esc(p.stage)}…</div>`;
  }

  /** The host validated a settings file; show what importing would change and send the answer. */
  async function showImportPreview(msg) {
    const n = (v) => Number(v || 0).toLocaleString();
    const rot = msg.rotation || {};
    const pins = msg.pins || {};
    const details = [
      `Rotation: ${n(rot.before)} → ${n(rot.after)} clips (${n(rot.added)} added, ${n(rot.removed)} removed)`,
      `Kept offline: ${n(pins.before)} → ${n(pins.after)} clips`,
      ...(msg.changes || []),
    ];
    if (rot.notInCatalogue) {
      details.push({ warn: false, text: `${n(rot.notInCatalogue)} rotation clip${rot.notInCatalogue === 1 ? ' is' : 's are'} not in this catalogue yet; they stay in the rotation and play once a refresh lists them.` });
    }
    if (pins.notInCatalogue) {
      details.push({ warn: true, text: `${n(pins.notInCatalogue)} kept-offline clip${pins.notInCatalogue === 1 ? ' is' : 's are'} not in this catalogue and will be skipped.` });
    }
    if (pins.after && !msg.pinsFit) {
      details.push({ warn: true, text: `Keeping those clips offline needs about ${msg.pinsRequiredGb} GB, over the ${msg.budgetGb} GB cache limit. Everything else is imported; you will be asked about raising the limit.` });
    }
    if (msg.newerFormat) {
      details.push({ warn: true, text: 'This file was made by a newer version; anything this version does not know is ignored.' });
    }

    const when = msg.exportedUtc ? new Date(msg.exportedUtc).toLocaleDateString() : 'an unknown date';
    const machine = msg.machineChanges || [];
    const checkbox = msg.hasMachine && machine.length
      ? { label: `Also apply this file’s machine settings (${machine.join('; ')})`, checked: false }
      : null;

    const answer = await confirmDialog(
      `Import “${msg.fileName}”, exported ${when}${msg.appVersion ? ' by Win4KSS ' + msg.appVersion : ''}? ` +
      'This replaces your rotation, kept-offline clips and auto-add sources. Your current settings are copied to settings.before-import.json first.',
      [{ label: 'Import', value: 'yes', accent: true }, { label: 'Cancel', value: 'no' }],
      { details, checkbox });
    Bridge.send('confirmImport', { apply: answer === 'yes', includeMachine: answer === 'yes' && confirmDialog.checked === true });
  }

  /** "CTRL+ALT+S" or "Alt+Ctrl+S" (what a .lnk reports) -> "Ctrl+Alt+S". */
  function formatHotkey(text) {
    if (!text) return '';
    const parts = text.split('+').map((p) => p.trim()).filter(Boolean);
    const mods = ['CTRL', 'ALT', 'SHIFT'].filter((m) => parts.some((p) => p.toUpperCase() === m || (m === 'CTRL' && p.toUpperCase() === 'CONTROL')));
    const keys = parts.filter((p) => !['CTRL', 'CONTROL', 'ALT', 'SHIFT'].includes(p.toUpperCase()));
    return mods.map((m) => m[0] + m.slice(1).toLowerCase()).concat(keys).join('+');
  }

  function setText(id, text) {
    const el = document.getElementById(id);
    if (el) el.textContent = text === null || text === undefined ? '' : String(text);
  }

  function syncSettingsValues() {
    if (!settingsBuilt || !Store.settings) return;
    const s = Store.settings;
    const active = document.activeElement;

    const setIfIdle = (id, apply) => {
      const el = document.getElementById(id);
      if (el && el !== active) apply(el);
    };

    setIfIdle('set-quality', (el) => { el.value = s.quality; });
    setIfIdle('set-multimonitor', (el) => { el.value = s.multiMonitorMode; });
    setIfIdle('set-shuffle', (el) => { el.checked = !!s.shuffle; });
    setIfIdle('set-timeofday', (el) => { el.checked = !!s.useTimeOfDay; });
    setIfIdle('set-cacheenabled', (el) => { el.checked = !!s.cacheEnabled; });
    setIfIdle('set-cachedir', (el) => { el.value = s.cacheDirectory || ''; });
    setIfIdle('set-ffmpeg', (el) => { el.value = s.ffmpegPath || ''; });
    setIfIdle('set-budget', (el) => {
      el.max = String(Math.max(500, Math.ceil(s.cacheBudgetGb)));
      el.value = s.cacheBudgetGb;
      setText('set-budget-readout', `${Math.round(s.cacheBudgetGb)} GB`);
    });
    setIfIdle('set-autoupdate', (el) => { el.checked = !!s.autoCheckForUpdates; });
    setIfIdle('set-shortcut-desktop', (el) => { el.checked = !!s.shortcutDesktop; });
    setIfIdle('set-shortcut-startmenu', (el) => { el.checked = !!s.shortcutStartMenu; });
    setIfIdle('set-hotkey', (el) => { el.value = formatHotkey(s.hotkey); });
    const hasShortcut = !!(s.shortcutDesktop || s.shortcutStartMenu);
    for (const id of ['set-hotkey', 'hotkey-clear']) {
      const el = document.getElementById(id);
      if (el) el.disabled = !hasShortcut;
    }

    const c = s.previewCapabilities || {};
    setText('preview-caps', c.ffmpegAvailable
      ? `ffmpeg found${c.ffmpegPath ? ' at ' + c.ffmpegPath : ''} \u2014 hover clips are on.`
      : 'ffmpeg not found \u2014 stills come from the catalogues\u2019 thumbnails and there are no hover clips.');

    const a = Store.about || {};
    setText('about-version', a.version);
    setText('about-mpv', a.mpvVersion);
    setText('about-data', a.dataFolder);
    setText('about-settings', a.settingsFile);
    setText('about-log', a.logFile);
    setText('about-mpvlog', a.mpvLog);
    setText('about-cache', a.cacheFolder);
    setText('about-previews', a.previewsFolder);

    renderFolderList();
    renderSourceToggles();
    renderCacheUsage();
    renderUpdateStatus();
    renderInstallProgress();
  }

  function renderCacheUsage() {
    const bar = document.getElementById('cache-bar');
    if (!bar || !Store.cacheStats) return;
    const { pinnedBytes, rollingBytes, budgetBytes, freeDiskBytes, pinnedFiles, rollingFiles, pendingPins, ownsPins } = Store.cacheStats;
    const budget = budgetBytes || 1;
    const pinnedPct = Math.min(100, (pinnedBytes / budget) * 100);
    const rollingPct = Math.min(100 - pinnedPct, (rollingBytes / budget) * 100);
    const freePct = Math.max(0, 100 - pinnedPct - rollingPct);
    bar.innerHTML =
      `<div class="seg-pinned" style="width:${pinnedPct}%"></div>` +
      `<div class="seg-rolling" style="width:${rollingPct}%"></div>` +
      `<div class="seg-free" style="width:${freePct}%"></div>`;

    setText('cache-legend-free',
      `${formatBytes(pinnedBytes)} kept + ${formatBytes(rollingBytes)} rolling of ${formatBytes(budgetBytes)} \u00b7 ` +
      `${formatBytes(freeDiskBytes)} free on disk`);
    const pins = [`${(pinnedFiles || 0).toLocaleString()} kept offline`, `${(rollingFiles || 0).toLocaleString()} in the rolling cache`];
    if (pendingPins) pins.push(`${pendingPins.toLocaleString()} waiting to download${ownsPins === false ? ' (the screen saver is downloading them)' : ''}`);
    setText('cache-pins', pins.join(' \u00b7 ') + '.');
  }

  // ======================================================================================
  // 13. Toasts and the confirm dialog
  // ======================================================================================

  function showToast(message, opts) {
    const options = opts || {};
    const host = document.getElementById('toast-host');
    const el = document.createElement('div');
    el.className = `toast toast-${options.level || 'info'}`;
    el.innerHTML = '<div class="toast-msg"></div><div class="toast-actions"></div><button type="button" class="toast-close" aria-label="Dismiss">\u2715</button>';
    el.querySelector('.toast-msg').textContent = message;
    const actionsEl = el.querySelector('.toast-actions');
    const remove = () => { if (el.parentElement) { el.remove(); schedulePanelUpdate(); } };
    for (const action of options.actions || []) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'btn';
      btn.textContent = action.label;
      btn.addEventListener('click', () => { action.onClick(); remove(); });
      actionsEl.appendChild(btn);
    }
    if (!actionsEl.children.length) actionsEl.remove();
    el.querySelector('.toast-close').addEventListener('click', remove);
    host.appendChild(el);
    schedulePanelUpdate();
    setTimeout(remove, options.actions && options.actions.length ? 20000 : options.level === 'error' ? 12000 : 6000);
  }

  /** An in-page modal (a native confirm() would also work, but this one hides the native
   *  preview panel and matches the theme). Resolves to the chosen button's value, or null. */
  function confirmDialog(message, buttons, opts) {
    return new Promise((resolve) => {
      const overlay = document.getElementById('confirm-overlay');
      const actions = document.getElementById('confirm-actions');
      const extra = document.getElementById('confirm-extra');
      document.getElementById('confirm-message').textContent = message;
      actions.innerHTML = '';
      extra.innerHTML = '';
      extra.classList.add('hidden');

      // Optional: opts.details (strings, or {text, warn}) as a list, opts.checkbox {label, checked}.
      // The checkbox's final state is left in confirmDialog.checked for the caller.
      let box = null;
      const details = (opts && opts.details) || [];
      if (details.length) {
        const list = document.createElement('ul');
        list.className = 'confirm-details';
        for (const d of details) {
          const li = document.createElement('li');
          li.textContent = typeof d === 'string' ? d : d.text;
          if (typeof d !== 'string' && d.warn) li.className = 'is-warning';
          list.appendChild(li);
        }
        extra.appendChild(list);
      }
      if (opts && opts.checkbox) {
        const label = document.createElement('label');
        label.className = 'confirm-check';
        box = document.createElement('input');
        box.type = 'checkbox';
        box.checked = !!opts.checkbox.checked;
        const text = document.createElement('span');
        text.textContent = opts.checkbox.label;
        label.append(box, text);
        extra.appendChild(label);
      }
      if (extra.children.length) extra.classList.remove('hidden');
      confirmDialog.checked = false;

      const finish = (value) => {
        confirmDialog.checked = box ? box.checked : false;
        overlay.classList.add('hidden');
        extra.classList.add('hidden');
        document.removeEventListener('keydown', onKey, true);
        schedulePanelUpdate();
        resolve(value);
      };
      const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); finish(null); } };
      for (const b of buttons) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn' + (b.accent ? ' btn-accent' : '') + (b.danger ? ' btn-danger' : '');
        btn.textContent = b.label;
        btn.addEventListener('click', () => finish(b.value));
        actions.appendChild(btn);
      }
      document.addEventListener('keydown', onKey, true);
      overlay.classList.remove('hidden');
      updatePanel();
      actions.firstChild?.focus();
    });
  }

  // ======================================================================================
  // 14. Host -> page message handlers
  // ======================================================================================

  function pruneRemovedClips() {
    for (const key of Array.from(Store.preview.keys())) if (!Store.clips.has(key)) Store.preview.delete(key);
    for (const key of Array.from(Store.selection)) if (!Store.clips.has(key)) Store.selection.delete(key);
    for (const key of Array.from(Store.requestedStill)) if (!Store.clips.has(key)) Store.requestedStill.delete(key);
  }

  function registerHostHandlers() {
    Bridge.on('catalog', (msg) => {
      // Sent after `ready` (start-up or a reload) and after every refresh or folder change. A
      // full stateDelta always follows, so state starts from scratch; previews and the
      // selection survive for clips that are still there.
      Store.clips.clear();
      Store.state.clear();
      Store.order = [];
      for (const clip of msg.clips) { Store.clips.set(clip.key, clip); Store.order.push(clip.key); }
      Store.catalogLoaded = true;
      pruneRemovedClips();
      setRefreshing(false);
      if (Store.detailKey && !Store.clips.has(Store.detailKey)) Store.selection.delete(Store.detailKey);
      onFiltersChanged();
      applySelectionChanged();
      if (Store.detailKey) openDetail(Store.detailKey);
      if (settingsBuilt) renderSourceToggles();
    });

    Bridge.on('stateDelta', (msg) => {
      const changed = [];
      let flagsChanged = false;
      let sortChanged = false;
      for (const [key, patch] of Object.entries(msg.clips)) {
        const prev = Store.state.get(key);
        const next = Object.assign({}, prev || {}, patch);
        Store.state.set(key, next);
        changed.push(key);
        if (stateFlags(prev) !== stateFlags(next)) flagsChanged = true;
        if (Store.sort.field === 'lastPlayed' && (prev || {}).lastPlayedUtc !== next.lastPlayedUtc) sortChanged = true;
        if (Store.detailKey === key) renderDetailState(key);
      }
      if (flagsChanged || sortChanged) {
        recomputeVisible();
        refreshGrid(changed);
        if (flagsChanged) renderFacets();
        if (settingsBuilt && flagsChanged) renderSourceToggles();
      } else {
        for (const key of changed) updateCardVisuals(key);
      }
    });

    Bridge.on('previewReady', (msg) => {
      const pv = Store.preview.get(msg.key) || {};
      const kind = msg.kind.toLowerCase();
      pv[kind] = {
        status: msg.status,
        url: msg.status === 'Ready' ? msg.url : null,
        color: msg.placeholderColor,
      };
      Store.preview.set(msg.key, pv);
      if (kind === 'still') updateCardThumb(msg.key);
      if (Store.detailKey === msg.key) updateDetailPreview();
      if (kind === 'hover') {
        if (hoverPendingKey === msg.key) hoverPendingKey = null;
        const cardEl = cardEls.get(msg.key);
        cardEl?.querySelector('.thumb')?.classList.remove('is-loading');
        if (hoverKey === msg.key && msg.status === 'Ready' && msg.url && cardEl) showHoverVideo(cardEl, msg.url);
      }
    });

    Bridge.on('settings', (msg) => {
      Store.settings = msg.values;
      Store.sources = msg.sources || [];
      Store.about = msg.about || null;
      if (settingsBuilt) syncSettingsValues();
      else if (!document.getElementById('settings-overlay').classList.contains('hidden')) buildSettingsPanes();
    });

    Bridge.on('cacheStats', (msg) => {
      Store.cacheStats = msg;
      renderCacheUsage();
      if (Store.detailKey) renderDetailDownload(Store.state.get(Store.detailKey) || {});
    });

    Bridge.on('pinRefused', (msg) => {
      showToast(
        `Keeping ${msg.keys.length.toLocaleString()} clip${msg.keys.length === 1 ? '' : 's'} offline needs ${msg.requiredGb} GB, ` +
        `over the ${msg.budgetGb} GB cache limit (${msg.shortfallGb} GB short).`,
        {
          level: 'warning',
          actions: [
            { label: `Raise limit to ${msg.requiredGb} GB`, onClick: () => {
              Bridge.send('saveSettings', { patch: { cacheBudgetGb: msg.requiredGb } });
              Bridge.send('setPinned', { keys: msg.keys, value: true });
            } },
            { label: 'Cancel', onClick: () => {} },
          ],
        });
      // The switch flipped on optimistically; put it back.
      if (Store.detailKey) renderDetailState(Store.detailKey);
    });

    Bridge.on('refreshProgress', (msg) => {
      if (msg.running) {
        const name = sourceName(msg.sourceId) || msg.sourceId || '';
        const count = msg.total ? ` ${(msg.done || 0).toLocaleString()}/${msg.total.toLocaleString()}` : '';
        setRefreshing(true, `${name}${msg.stage ? ': ' + msg.stage : ''}${count}`);
        return;
      }
      setRefreshing(false);
      if (msg.error) {
        showToast(`Catalogue refresh failed: ${msg.error}`, { level: 'error' });
        return;
      }
      const sources = msg.sources || [];
      const added = sources.reduce((n, s) => n + (s.added || 0), 0);
      const removed = sources.reduce((n, s) => n + (s.removed || 0), 0);
      const failed = sources.filter((s) => !s.succeeded);
      let text = `Catalogues refreshed: ${added.toLocaleString()} new clip${added === 1 ? '' : 's'}` +
        (removed ? `, ${removed.toLocaleString()} gone` : '') + '.';
      if (failed.length) text += ` Failed: ${failed.map((s) => `${sourceName(s.sourceId) || s.sourceId}${s.error ? ' (' + s.error + ')' : ''}`).join(', ')}.`;
      showToast(text, { level: failed.length ? 'warning' : 'info' });
    });

    Bridge.on('importPreview', (msg) => { showImportPreview(msg); });

    Bridge.on('updateStatus', (msg) => {
      Store.updateStatus = msg;
      renderUpdateStatus();
    });

    Bridge.on('updateInstallProgress', (msg) => {
      Store.updateInstallProgress = msg;
      renderInstallProgress();
    });

    Bridge.on('notice', (msg) => showToast(msg.text, { level: msg.level }));
  }

  // ======================================================================================
  // 15. Init
  // ======================================================================================

  function wireGrid() {
    const gridEl = document.getElementById('grid');
    gridEl.addEventListener('click', (e) => {
      const cardEl = e.target.closest('.card');
      if (!cardEl) return;
      handleCardClick(cardEl.dataset.key, e);
    });
    gridEl.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      const cardEl = e.target.closest('.card');
      if (!cardEl) return;
      e.preventDefault();
      handleCardClick(cardEl.dataset.key, e);
    });
    document.getElementById('grid-wrap').addEventListener('click', (e) => {
      if (e.target.id === 'grid-wrap' || e.target.id === 'grid') clearSelection();
    });
    wireGridHover(gridEl);

    stillObserver = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        stillObserver.unobserve(entry.target);
        requestStillIfNeeded(entry.target.dataset.key);
      }
    }, { root: document.getElementById('grid-wrap'), rootMargin: '600px 0px' });
  }

  function wireDetail() {
    document.getElementById('detail-close').addEventListener('click', clearSelection);
    document.getElementById('toggle-rotation').addEventListener('change', (e) => {
      if (Store.detailKey) Bridge.send('setRotation', { keys: [Store.detailKey], value: e.target.checked });
    });
    document.getElementById('toggle-pin').addEventListener('change', (e) => {
      if (Store.detailKey) requestPin([Store.detailKey], e.target.checked);
    });
    document.getElementById('detail-download-retry').addEventListener('click', () => Bridge.send('retryDownloads', {}));

    // Anything that can move or cover the video box re-sends panelRect (coalesced per frame).
    window.addEventListener('resize', schedulePanelUpdate);
    document.getElementById('detail').addEventListener('scroll', schedulePanelUpdate, { passive: true });
    document.addEventListener('visibilitychange', schedulePanelUpdate);
    if (window.ResizeObserver) {
      const ro = new ResizeObserver(schedulePanelUpdate);
      ro.observe(document.getElementById('detail-preview-rect'));
      ro.observe(document.getElementById('detail'));
    }
  }

  function wireGlobalKeys() {
    document.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      if (!document.getElementById('settings-overlay').classList.contains('hidden')) { closeSettings(); return; }
      if (document.getElementById('sidebar').classList.contains('is-open')) {
        document.getElementById('sidebar').classList.remove('is-open');
        schedulePanelUpdate();
        return;
      }
      if (Store.selection.size > 0) clearSelection();
    });
  }

  function init() {
    wireToolbar();
    wireSidebar();
    wireGrid();
    wireDetail();
    wireBulkBar();
    wireSettingsStatic();
    wireGlobalKeys();
    registerHostHandlers();
    updateGridStatus();
    // Last: every handler is in place before the host is told it may start posting.
    Bridge.connect();
    // After a reload the host may still be playing what the old page selected, in a panel
    // placed for the old layout: start from nothing selected and the panel hidden.
    Bridge.send('selectClip', { key: null });
    updatePanel();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
