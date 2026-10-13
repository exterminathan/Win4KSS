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
//   1. Bridge            -- thin wrapper over window.chrome.webview
//   2. Constants          -- facet order, state-tag strings, display-name tables
//   3. Store              -- all mutable client state, in one place
//   4. Tag / filter model -- combining catalogue tags with live state, faceted counts
//   5. Sorting
//   6. Grid rendering
//   7. Hover preview
//   8. Detail pane
//   9. Selection / bulk bar
//  10. Sidebar (facets + search)
//  11. Toolbar (sort, view size, refresh, start now)
//  12. Settings panel
//  13. Toast
//  14. Host -> page message handlers
//  15. Init

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

    if (webview()) {
      webview().addEventListener('message', (e) => dispatch(e.data));
    } else {
      // eslint-disable-next-line no-console
      console.warn('[app] window.chrome.webview is not present -- running with no host. ' +
        'Open this page through docs/browser-mock/dev.html for a working mock, or inside WebView2.');
    }

    function dispatch(msg) {
      if (!msg || typeof msg.type !== 'string') return;
      const fn = handlers.get(msg.type);
      if (fn) fn(msg);
      // Unknown message types (e.g. a future refreshProgress shape) are fine to ignore.
    }

    return {
      on(type, fn) { handlers.set(type, fn); },
      send(type, payload) {
        const wv = webview();
        if (!wv) {
          // eslint-disable-next-line no-console
          console.warn('[app] no host to send to', type, payload);
          return;
        }
        wv.postMessage(Object.assign({ type }, payload));
      },
    };
  })();

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

  // Mirrors TagBuilder.Values -- the live State facet's exact strings.
  const STATE_VALUES = {
    IN_ROTATION: 'In rotation', KEPT_OFFLINE: 'Kept offline', CACHED: 'Cached',
    DOWNLOADING: 'Downloading', NEW: 'New', UNHEALTHY: 'Unhealthy',
  };

  // Mirrors TagBuilder.SourceName, keyed by the *last* ':'-delimited segment of a source id
  // (availableSources mixes bare ids like "apple" with compound ones like
  // "aerial-manifests:apple-tvos26"; the segment after the colon is what SourceName switches on).
  const SOURCE_DISPLAY_NAMES = {
    apple: 'Apple', 'apple-tvos26': 'Apple', 'archive-org': 'Internet Archive', nasa: 'NASA',
    'amazon-fireos8': 'Amazon', 'jetson-creative': 'Jetson', 'robin-fourcade': 'Robin', local: 'Local',
  };

  const RESOLUTION_RANK = { '1080p': 1, '4K': 2, '5K+': 3, '8K': 4 };

  // ======================================================================================
  // 3. Store
  // ======================================================================================

  const Store = {
    clips: new Map(),          // key -> catalog clip record
    order: [],                 // keys, catalog arrival order (stable fallback sort)
    state: new Map(),          // key -> stateDelta record (inRotation, pinned, cached, download, health, isNew)
    preview: new Map(),        // key -> { still: {status,url,color}, hover: {status,url,color} }
    settings: null,            // settings.values, or null until the first `settings` message
    cacheStats: null,          // cacheStats message, or null until the first one arrives

    visible: [],               // keys passing the current filters, sorted
    filters: { search: '', facets: new Map() }, // facetName -> Set<value>
    collapsedFacets: new Set(),
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
   *  TagBuilder.Build's State block exactly -- including trusting `isNew` as given; the
   *  page never re-derives "new" from addedUtc itself (that's TagBuilder.IsNew's job,
   *  server-side, per docs/browser-bridge-contract.md). */
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

  function matchesSearch(clip, text) {
    if (!text) return true;
    const haystack = [clip.title, clip.category, clip.subcategory, clip.creator]
      .concat(clip.subjects || [])
      .filter(Boolean)
      .join(' \u0000 ')
      .toLowerCase();
    return haystack.includes(text);
  }

  /** True if `key` satisfies every active facet filter except `excludeFacet` (OR within a
   *  facet's selected values, AND across facets). Passing null/undefined applies all of them. */
  function matchesFacetsExcept(key, excludeFacet) {
    for (const [facet, values] of Store.filters.facets) {
      if (facet === excludeFacet || !values || values.size === 0) continue;
      const tags = combinedTags(key).filter((t) => t.facet === facet);
      if (!tags.some((t) => values.has(t.value))) return false;
    }
    return true;
  }

  function passesAllFilters(key) {
    const clip = Store.clips.get(key);
    return !!clip && matchesSearch(clip, Store.filters.search) && matchesFacetsExcept(key, null);
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
        if (!matchesFacetsExcept(key, facet)) continue;
        const counts = result.get(facet);
        const here = new Set(tags.filter((t) => t.facet === facet).map((t) => t.value));
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
    const an = a === null || a === undefined;
    const bn = b === null || b === undefined;
    if (an && bn) return 0;
    if (an) return 1;
    if (bn) return -1;
    if (a < b) return dir === 'asc' ? -1 : 1;
    if (a > b) return dir === 'asc' ? 1 : -1;
    return 0;
  }

  function compareKeys(aKey, bKey) {
    const a = Store.clips.get(aKey);
    const b = Store.clips.get(bKey);
    const { field, dir } = Store.sort;
    let av = null, bv = null;

    switch (field) {
      case 'title':
        av = (a.title || '').toLowerCase(); bv = (b.title || '').toLowerCase(); break;
      case 'durationSeconds':
        av = a.durationSeconds; bv = b.durationSeconds; break;
      case 'sizeBytes':
        av = a.sizeBytes; bv = b.sizeBytes; break;
      case 'resolution':
        av = resolutionRank(a); bv = resolutionRank(b); break;
      case 'addedUtc':
        av = a.addedUtc ? Date.parse(a.addedUtc) : null; bv = b.addedUtc ? Date.parse(b.addedUtc) : null; break;
      case 'source':
        av = sourceDisplayName(a).toLowerCase(); bv = sourceDisplayName(b).toLowerCase(); break;
      case 'lastPlayed':
        // TODO(local): no last-played timestamp crosses the bridge -- CacheEntry.LastPlayedUtc
        // (src/Win4K.Core/Caching/CacheIndex.cs) is host-only and isn't in the stateDelta/catalog
        // contract. Falls back to stable catalogue order until/unless the contract grows a field
        // for it. The sort dropdown still offers it since the plan asks for it explicitly.
        return 0;
      default:
        return 0;
    }
    return cmpNullsLast(av, bv, dir);
  }

  function recomputeVisible() {
    const arr = [];
    for (const key of Store.order) if (passesAllFilters(key)) arr.push(key);
    arr.sort(compareKeys);
    Store.visible = arr;
  }

  // ======================================================================================
  // 6. Grid rendering
  // ======================================================================================

  const cardEls = new Map(); // key -> element, rebuilt alongside Store.visible
  let stillObserver = null;

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

  function tagValue(clip, facet) {
    const t = (clip.tags || []).find((tag) => tag.facet === facet);
    return t ? t.value : null;
  }

  function sourceDisplayName(clip) {
    const fromTag = tagValue(clip, 'Source');
    if (fromTag) return fromTag;
    return sourceIdDisplayName(clip.sourceId);
  }

  /** Display name for a raw/compound source id ("apple", "aerial-manifests:apple-tvos26"). */
  function sourceIdDisplayName(id) {
    if (!id) return '';
    const segment = id.includes(':') ? id.slice(id.indexOf(':') + 1) : id;
    const name = SOURCE_DISPLAY_NAMES[segment] || segment;
    return id.startsWith('aerial-manifests:') ? `${name} (Aerial Manifests)` : name;
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
    if (state.pinned) icons.push('<span class="state-icon pin" title="Kept offline">\u2B07</span>');
    if (state.health === 'Unhealthy') icons.push('<span class="state-icon unhealthy" title="Unhealthy">!</span>');
    if (state.isNew) icons.push('<span class="state-icon new" title="New">\u2605</span>');
    const iconsEl = el.querySelector('.state-icons');
    if (iconsEl) iconsEl.innerHTML = icons.join('');

    const thumb = el.querySelector('.thumb');
    let ring = thumb.querySelector('.progress-ring');
    if (state.download && state.download.state === 'Downloading') {
      if (!ring) { ring = document.createElement('div'); ring.className = 'progress-ring'; thumb.appendChild(ring); }
      const pct = state.download.fraction !== null && state.download.fraction !== undefined
        ? Math.round(state.download.fraction * 100) : 0;
      ring.style.setProperty('--pct', String(pct));
      ring.title = `${pct}%`;
    } else if (ring) {
      ring.remove();
    }
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
        thumb.insertBefore(img, thumb.firstChild);
      }
      if (img.src !== pv.still.url) img.src = pv.still.url;
      img.classList.remove('hidden');
      thumb.querySelector('.thumb-placeholder')?.classList.add('hidden');
    } else if (img) {
      img.classList.add('hidden');
    }
  }

  function buildGrid() {
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

    document.getElementById('grid-empty').classList.toggle('hidden', Store.visible.length > 0);
    document.getElementById('grid-status').textContent =
      `${Store.visible.length.toLocaleString()} of ${Store.clips.size.toLocaleString()} clips`;

    observeCards();
  }

  function observeCards() {
    if (!stillObserver) return;
    for (const el of cardEls.values()) stillObserver.observe(el);
  }

  function requestStillIfNeeded(key) {
    if (Store.requestedStill.has(key)) return;
    Store.requestedStill.add(key);
    Bridge.send('requestPreviews', { keys: [key], kinds: 'Still' });
  }

  // ======================================================================================
  // 7. Hover preview
  //
  // One shared <video> is moved into whichever card's .thumb is currently hovered, rather
  // than giving every one of up to ~1,500 cards its own <video> element.
  // ======================================================================================

  let hoverVideoEl = null;
  let hoverKey = null;
  let hoverTimer = null;

  function clearHoverTimer() {
    if (hoverTimer) { clearTimeout(hoverTimer); hoverTimer = null; }
  }

  function beginHoverIntent(key, cardEl) {
    hoverKey = key;
    const pv = Store.preview.get(key);
    if (pv && pv.hover && pv.hover.status === 'Ready' && pv.hover.url) {
      showHoverVideo(cardEl, pv.hover.url);
      return;
    }
    if (!pv || !pv.hover || (pv.hover.status !== 'Pending' && pv.hover.status !== 'Unavailable')) {
      Bridge.send('requestPreviews', { keys: [key], kinds: 'Hover' });
    }
  }

  function showHoverVideo(cardEl, url) {
    const thumb = cardEl.querySelector('.thumb');
    if (!thumb) return;
    if (hoverVideoEl.src !== url) hoverVideoEl.src = url;
    thumb.appendChild(hoverVideoEl);
    hoverVideoEl.classList.remove('hidden');
    hoverVideoEl.currentTime = 0;
    hoverVideoEl.play().catch(() => { /* autoplay may be blocked; the still stays visible underneath */ });
  }

  function endHover() {
    hoverKey = null;
    hoverVideoEl.pause();
    hoverVideoEl.classList.add('hidden');
    if (hoverVideoEl.parentElement) hoverVideoEl.parentElement.removeChild(hoverVideoEl);
  }

  function wireGridHover(gridEl) {
    hoverVideoEl = document.createElement('video');
    hoverVideoEl.className = 'thumb-media hidden';
    hoverVideoEl.muted = true;
    hoverVideoEl.loop = true;
    hoverVideoEl.playsInline = true;

    gridEl.addEventListener('mouseover', (e) => {
      const cardEl = e.target.closest('.card');
      if (!cardEl || cardEl.dataset.key === hoverKey) return;
      clearHoverTimer();
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
  // 8. Detail pane
  // ======================================================================================

  function openDetail(key) {
    Store.detailKey = key;
    const detailEl = document.getElementById('detail');
    detailEl.classList.remove('hidden');

    const clip = Store.clips.get(key);
    const state = Store.state.get(key) || {};
    if (!clip) return;

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

    const rotationEl = document.getElementById('toggle-rotation');
    const pinEl = document.getElementById('toggle-pin');
    rotationEl.checked = !!state.inRotation;
    pinEl.checked = !!state.pinned;
    // PinManager.ClipFor returns null for local clips -- nothing to download, so pinning
    // one is a no-op the host silently ignores (PinCheck.Skipped). Disable it here instead
    // of letting the user discover that by nothing happening.
    pinEl.disabled = !!clip.isLocal;
    renderDetailDownload(state);
    renderDetailTags(key);

    updateDetailPreview();
    markCardActive(key);
    Bridge.send('selectClip', { key });
    Bridge.send('requestPreviews', { keys: [key], kinds: 'Both' });
    sendPanelRect();
  }

  function closeDetail() {
    if (Store.detailKey === null) return;
    Store.detailKey = null;
    document.getElementById('detail').classList.add('hidden');
    Bridge.send('selectClip', { key: null });
    markCardActive(null);
    stopDetailPreview();
    sendPanelRect();
  }

  function renderDetailDownload(state) {
    const el = document.getElementById('detail-download');
    if (state.download) {
      const pct = state.download.fraction !== null && state.download.fraction !== undefined
        ? `${Math.round(state.download.fraction * 100)}%` : '';
      const bytes = state.download.totalBytes
        ? `${formatBytes(state.download.bytesReceived)} of ${formatBytes(state.download.totalBytes)}`
        : formatBytes(state.download.bytesReceived);
      el.textContent = `Downloading\u2026 ${pct} (${bytes})`.replace('  ', ' ');
      el.classList.remove('hidden');
    } else if (state.pinned && state.cached) {
      el.textContent = 'Kept offline \u2014 downloaded.';
      el.classList.remove('hidden');
    } else if (state.pinned) {
      el.textContent = 'Kept offline \u2014 queued.';
      el.classList.remove('hidden');
    } else {
      el.classList.add('hidden');
    }
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
      chip.title = `Filter by ${FACET_LABELS[tag.facet] || tag.facet}`;
      chip.addEventListener('click', () => toggleFacetValue(tag.facet, tag.value, !isFacetValueActive(tag.facet, tag.value)));
      tagsEl.appendChild(chip);
    }
  }

  function refreshDetailTagChips() {
    if (Store.detailKey === null) return;
    const tagsEl = document.getElementById('detail-tags');
    for (const chip of tagsEl.children) {
      const tag = combinedTags(Store.detailKey).find((t) => t.value === chip.textContent);
      chip.classList.toggle('is-active', !!tag && isFacetValueActive(tag.facet, tag.value));
    }
  }

  function updateDetailPreview() {
    const key = Store.detailKey;
    if (!key) return;
    const pv = Store.preview.get(key) || {};
    const placeholderEl = document.getElementById('detail-preview-placeholder');
    const imgEl = document.getElementById('detail-preview-img');
    const videoEl = document.getElementById('detail-preview-video');
    const color = (pv.still && pv.still.color) || (pv.hover && pv.hover.color);
    if (color) placeholderEl.style.backgroundColor = color;

    if (pv.hover && pv.hover.status === 'Ready' && pv.hover.url) {
      if (videoEl.src !== pv.hover.url) videoEl.src = pv.hover.url;
      videoEl.classList.remove('hidden');
      videoEl.play().catch(() => {});
      imgEl.classList.add('hidden');
    } else if (pv.still && pv.still.status === 'Ready' && pv.still.url) {
      videoEl.classList.add('hidden');
      videoEl.pause();
      if (imgEl.src !== pv.still.url) imgEl.src = pv.still.url;
      imgEl.classList.remove('hidden');
    } else {
      videoEl.classList.add('hidden');
      videoEl.pause();
      imgEl.classList.add('hidden');
    }
  }

  function stopDetailPreview() {
    const imgEl = document.getElementById('detail-preview-img');
    const videoEl = document.getElementById('detail-preview-video');
    videoEl.pause();
    videoEl.removeAttribute('src');
    videoEl.classList.add('hidden');
    imgEl.classList.add('hidden');
  }

  let lastActiveCardKey = null;
  function markCardActive(key) {
    if (lastActiveCardKey) {
      const prev = cardEls.get(lastActiveCardKey);
      if (prev) prev.classList.remove('is-active');
    }
    if (key) {
      const el = cardEls.get(key);
      if (el) el.classList.add('is-active');
    }
    lastActiveCardKey = key;
  }

  /** Tells the host where to park its native mpv preview HWND, in page-relative CSS
   *  pixels (docs/browser-bridge-contract.md's `panelRect`). Sent on select/resize/scroll. */
  function sendPanelRect() {
    const detailEl = document.getElementById('detail');
    if (Store.detailKey === null || detailEl.classList.contains('hidden')) {
      Bridge.send('panelRect', { x: 0, y: 0, width: 0, height: 0, visible: false });
      return;
    }
    const rect = document.getElementById('detail-preview-rect').getBoundingClientRect();
    Bridge.send('panelRect', {
      x: Math.round(rect.left), y: Math.round(rect.top),
      width: Math.round(rect.width), height: Math.round(rect.height),
      visible: true,
    });
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
      openDetail(keys[0]);
    } else {
      closeDetail();
      bulkBarEl.classList.toggle('hidden', keys.length === 0);
      if (keys.length > 0) document.getElementById('bulk-count').textContent = `${keys.length.toLocaleString()} selected`;
    }
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
  // 10. Sidebar (facets + search)
  // ======================================================================================

  function renderFacets() {
    const counts = computeFacetCounts();
    const listEl = document.getElementById('facet-list');
    listEl.innerHTML = '';

    for (const facet of TAG_FACETS) {
      const valuesMap = counts.get(facet);
      if (!valuesMap || valuesMap.size === 0) continue;

      const group = document.createElement('div');
      group.className = 'facet-group' + (Store.collapsedFacets.has(facet) ? ' is-collapsed' : '');

      const head = document.createElement('button');
      head.type = 'button';
      head.className = 'facet-group-head';
      head.innerHTML = `<span>${esc(FACET_LABELS[facet] || facet)}</span><span class="chevron">\u25BC</span>`;
      head.addEventListener('click', () => {
        group.classList.toggle('is-collapsed');
        if (group.classList.contains('is-collapsed')) Store.collapsedFacets.add(facet);
        else Store.collapsedFacets.delete(facet);
      });
      group.appendChild(head);

      const list = document.createElement('div');
      list.className = 'facet-values';
      const activeSet = Store.filters.facets.get(facet);
      const sorted = Array.from(valuesMap.entries()).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));

      for (const [value, count] of sorted) {
        const active = !!(activeSet && activeSet.has(value));
        const row = document.createElement('label');
        row.className = 'facet-row' + (active ? ' is-active' : '');
        row.innerHTML = `<input type="checkbox" ${active ? 'checked' : ''}>` +
          `<span class="facet-value-name">${esc(value)}</span><span class="facet-value-count">${count}</span>`;
        row.querySelector('input').addEventListener('change', (e) => toggleFacetValue(facet, value, e.target.checked));
        list.appendChild(row);
      }

      group.appendChild(list);
      listEl.appendChild(group);
    }
  }

  function wireSidebar() {
    document.getElementById('clear-filters').addEventListener('click', () => {
      Store.filters.facets.clear();
      Store.filters.search = '';
      document.getElementById('search-input').value = '';
      onFiltersChanged();
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
        const gridEl = document.getElementById('grid');
        gridEl.className = 'grid grid-' + Store.viewSize;
      });
    }

    document.getElementById('select-all-filtered').addEventListener('click', () => {
      Store.selection = new Set(Store.visible);
      Store.lastClickedKey = null;
      applySelectionChanged();
    });

    document.getElementById('refresh-catalogs').addEventListener('click', () => {
      Bridge.send('refreshCatalogs', {});
      showRefreshSpinner(true);
    });

    document.getElementById('start-now').addEventListener('click', () => Bridge.send('startNow', {}));

    document.getElementById('open-settings').addEventListener('click', openSettings);
  }

  function showRefreshSpinner(on) {
    document.getElementById('refresh-spinner').classList.toggle('hidden', !on);
    document.getElementById('refresh-catalogs').disabled = on;
  }

  // ======================================================================================
  // 12. Settings panel
  // ======================================================================================

  let settingsBuilt = false;

  function openSettings() {
    document.getElementById('settings-overlay').classList.remove('hidden');
    if (!settingsBuilt && Store.settings) buildSettingsPanes();
    if (settingsBuilt) syncSettingsValues();
  }

  function closeSettings() {
    document.getElementById('settings-overlay').classList.add('hidden');
  }

  function wireSettingsStatic() {
    document.getElementById('settings-close').addEventListener('click', closeSettings);
    document.getElementById('settings-overlay').addEventListener('click', (e) => {
      if (e.target.id === 'settings-overlay') closeSettings();
    });
    for (const tab of document.querySelectorAll('.settings-tab')) {
      tab.addEventListener('click', () => {
        for (const t of document.querySelectorAll('.settings-tab')) t.classList.toggle('is-active', t === tab);
        for (const pane of document.querySelectorAll('.settings-pane')) {
          pane.classList.toggle('is-active', pane.dataset.tab === tab.dataset.tab);
        }
      });
    }
  }

  // Debounced settings writes: callers patch individual fields; the patch is merged and
  // flushed once typing/dragging settles, so a slider doesn't spam saveSettings.
  let pendingPatch = {};
  let saveTimer = null;
  function patchSettings(patch) {
    Object.assign(pendingPatch, patch);
    Object.assign(Store.settings, patch); // optimistic locally; the host echoes `settings` back anyway
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      Bridge.send('saveSettings', { patch: pendingPatch });
      pendingPatch = {};
    }, 400);
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

  function buildSourcesPane() {
    const p = pane('sources');
    p.innerHTML = `
      <h3>Local folders</h3>
      <p class="settings-hint">Folders of video files on this machine. Local clips bypass the download cache and cannot be kept offline (they're already on disk).</p>
      <ul id="folder-list" class="folder-list"></ul>
      <div class="settings-row">
        <input id="folder-input" type="text" placeholder="D:\\Videos\\Aerial" style="flex:1 1 auto;">
        <button id="folder-add" type="button" class="btn">Add</button>
      </div>
      <p class="settings-hint">
        TODO(local): the bridge contract has no folder-picker round trip (no page\u2192host
        message returns a path from a native dialog), so this types/pastes a path rather than
        browsing for one like the old SettingsForm's FolderBrowserDialog did.
      </p>

      <h3>Auto-add new clips</h3>
      <p class="settings-hint">When a catalogue refresh finds new clips from a source below, automatically put them in rotation.</p>
      <div id="source-toggle-list"></div>
    `;

    document.getElementById('folder-add').addEventListener('click', () => {
      const input = document.getElementById('folder-input');
      const value = input.value.trim();
      if (!value) return;
      const folders = (Store.settings.localFolders || []).slice();
      if (!folders.includes(value)) folders.push(value);
      input.value = '';
      patchSettings({ localFolders: folders });
      renderFolderList();
    });
  }

  function renderFolderList() {
    const listEl = document.getElementById('folder-list');
    if (!listEl) return;
    const folders = Store.settings.localFolders || [];
    listEl.innerHTML = folders.length
      ? folders.map((f, i) => `<li><span>${esc(f)}</span><button type="button" class="remove-folder" data-i="${i}">Remove</button></li>`).join('')
      : '<li style="color:var(--text-faint)">No local folders.</li>';
    for (const btn of listEl.querySelectorAll('.remove-folder')) {
      btn.addEventListener('click', () => {
        const i = Number(btn.dataset.i);
        const next = folders.slice(0, i).concat(folders.slice(i + 1));
        patchSettings({ localFolders: next });
        renderFolderList();
      });
    }
  }

  function clipCountForSource(sourceId, excludeInRotation) {
    let count = 0;
    for (const [key, clip] of Store.clips) {
      if (clip.sourceId !== sourceId && !(clip.sourceIds || []).includes(sourceId)) continue;
      if (excludeInRotation && Store.state.get(key)?.inRotation) continue;
      count++;
    }
    return count;
  }

  function renderSourceToggles() {
    const listEl = document.getElementById('source-toggle-list');
    if (!listEl) return;
    const available = Store.settings.availableSources || [];
    const autoAdd = new Set(Store.settings.autoAddSources || []);
    listEl.innerHTML = available.map((id) => `
      <div class="source-toggle-row">
        <span>${esc(sourceIdDisplayName(id))}</span>
        <label class="switch">
          <input type="checkbox" data-source="${esc(id)}" ${autoAdd.has(id) ? 'checked' : ''}>
          <span class="switch-track"></span>
        </label>
      </div>`).join('');

    for (const input of listEl.querySelectorAll('input[type="checkbox"]')) {
      input.addEventListener('change', () => {
        const sourceId = input.dataset.source;
        const turningOn = input.checked;
        if (!turningOn) {
          Bridge.send('setAutoAdd', { sourceId, value: false });
          return;
        }
        const newCount = clipCountForSource(sourceId, true);
        if (newCount === 0) {
          Bridge.send('setAutoAdd', { sourceId, value: true, includeExisting: false });
          return;
        }
        const includeExisting = window.confirm(
          `Also add the ${newCount.toLocaleString()} existing ${sourceIdDisplayName(sourceId)} clip${newCount === 1 ? '' : 's'} not currently in rotation?`);
        Bridge.send('setAutoAdd', { sourceId, value: true, includeExisting });
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
      <p class="settings-hint">HDR is PQ-encoded and gets tone-mapped on an SDR display, so SDR is the safer default.</p>

      <h3>Multiple displays</h3>
      <select id="set-multimonitor">
        <option value="MainOnly">Video on the main display only</option>
        <option value="SameOnEach">Same video on every display</option>
        <option value="DifferentVideos">A different video on each display</option>
        <option value="SpanAll">One video spanning all displays</option>
      </select>

      <h3>Order</h3>
      <label class="switch-row"><span class="switch"><input id="set-shuffle" type="checkbox"><span class="switch-track"></span></span><span class="switch-label">Shuffle the play order</span></label>
      <label class="switch-row"><span class="switch"><input id="set-timeofday" type="checkbox"><span class="switch-track"></span></span><span class="switch-label">Prefer clips matching the time of day</span></label>
      <p class="settings-hint">Only 4 of Apple's 114 clips carry a day/night label, so this nudges very little.</p>
    `;
    document.getElementById('set-quality').addEventListener('change', (e) => patchSettings({ quality: e.target.value }));
    document.getElementById('set-multimonitor').addEventListener('change', (e) => patchSettings({ multiMonitorMode: e.target.value }));
    document.getElementById('set-shuffle').addEventListener('change', (e) => patchSettings({ shuffle: e.target.checked }));
    document.getElementById('set-timeofday').addEventListener('change', (e) => patchSettings({ useTimeOfDay: e.target.checked }));
  }

  function buildCachePane() {
    const p = pane('cache');
    p.innerHTML = `
      <h3>Disk cache</h3>
      <label class="switch-row"><span class="switch"><input id="set-cacheenabled" type="checkbox"><span class="switch-track"></span></span><span class="switch-label">Keep watched clips on disk</span></label>

      <div class="settings-row">
        <span class="field-label">Folder</span>
        <input id="set-cachedir" type="text" style="flex:1 1 auto;">
      </div>
      <div class="settings-row">
        <span class="field-label">Budget (GB)</span>
        <input id="set-budget" type="range" min="1" max="500" step="1" style="flex:1 1 auto;">
        <span id="set-budget-readout" style="min-width:48px;text-align:right;"></span>
      </div>
      <p class="settings-hint">When the cache exceeds its budget the least recently played clips are deleted.</p>

      <div class="cache-bar" id="cache-bar"></div>
      <div class="cache-legend">
        <span><span class="dot pinned"></span>Kept offline</span>
        <span><span class="dot rolling"></span>Rolling cache</span>
        <span id="cache-legend-free"></span>
      </div>
      <div class="settings-row">
        <button id="cache-purge" type="button" class="btn">Purge rolling cache</button>
      </div>
      <p class="settings-hint">
        TODO(local): "Purge rolling cache" sends a <code>purgeCache</code> page\u2192host message
        that isn't in docs/browser-bridge-contract.md as written -- the contract's page\u2192host
        table has no entry for it, even though the plan's Cache tab calls for "purge non-pinned"
        and VideoCache.PurgeUnpinned() already exists server-side. Flagged for the host side to
        confirm/add rather than silently assumed.
      </p>

      <h3>Previews</h3>
      <div class="settings-row">
        <span class="field-label">ffmpeg path</span>
        <input id="set-ffmpeg" type="text" placeholder="C:\\ffmpeg\\ffmpeg.exe" style="flex:1 1 auto;">
      </div>
      <p id="preview-caps" class="settings-hint"></p>
    `;
    document.getElementById('set-cacheenabled').addEventListener('change', (e) => patchSettings({ cacheEnabled: e.target.checked }));
    document.getElementById('set-cachedir').addEventListener('change', (e) => patchSettings({ cacheDirectory: e.target.value }));
    document.getElementById('set-ffmpeg').addEventListener('change', (e) => patchSettings({ ffmpegPath: e.target.value || null }));
    document.getElementById('set-budget').addEventListener('input', (e) => {
      document.getElementById('set-budget-readout').textContent = `${e.target.value} GB`;
      patchSettings({ cacheBudgetGb: Number(e.target.value) });
    });
    document.getElementById('cache-purge').addEventListener('click', () => {
      if (window.confirm('Delete all rolling (non-pinned) cached video? Clips kept offline are not touched.')) {
        Bridge.send('purgeCache', {});
      }
    });
  }

  function buildTriggerPane() {
    const p = pane('trigger');
    p.innerHTML = `
      <h3>Start the screen saver on demand</h3>
      <p class="settings-hint">Useful when you're getting up to leave and don't want to wait out the idle timer.</p>
      <div class="settings-row"><button id="set-startnow" type="button" class="btn btn-accent">Start now</button></div>

      <h3>Shortcuts</h3>
      <p class="settings-hint">
        TODO(local): desktop/Start Menu shortcuts and the hotkey (today's <code>ShortcutManager</code>
        / <code>ApplyShortcuts</code> in SettingsForm.cs) aren't in docs/browser-bridge-contract.md
        at all -- there's no settings field reporting whether a shortcut exists, and no
        page\u2192host message to create/remove one or set the hotkey. Until the contract grows
        that, this tab can only trigger "Start now"; shortcut management stays wherever 67's
        BrowserForm/BrowserBridge decides to carry it over.
      </p>
    `;
    document.getElementById('set-startnow').addEventListener('click', () => Bridge.send('startNow', {}));
  }

  function buildAboutPane() {
    const p = pane('about');
    p.innerHTML = `
      <h3>Win4KSS</h3>
      <div class="about-block">
        4K aerial screen saver for Windows.<br><br>
        Cache folder: <code id="about-cachedir"></code><br>
        ffmpeg: <code id="about-ffmpeg"></code>
      </div>
      <p class="settings-hint">
        Apple's aerial CDN presents a certificate chaining to Apple Root CA, which Windows does
        not trust out of the box. Rather than disabling verification, this app pins Apple's real
        root for its own downloads and hands mpv a certificate bundle of every Windows-trusted
        root plus Apple's.
      </p>
      <p class="settings-hint">
        TODO(local): the old About tab also showed the libmpv API version and the log file paths
        (SettingsForm.cs's About tab, reading MpvPlayer.ApiVersionString / AppPaths.LogFile
        directly in-process). None of that crosses the bridge contract as written -- there's no
        message carrying it and no AppPaths equivalent reachable from JS. Left out rather than
        guessed; would need either a new field on \`settings\` or a dedicated message.
      </p>
    `;
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
    setIfIdle('set-budget', (el) => { el.value = s.cacheBudgetGb; });
    const readout = document.getElementById('set-budget-readout');
    if (readout) readout.textContent = `${Math.round(s.cacheBudgetGb)} GB`;

    const caps = document.getElementById('preview-caps');
    if (caps) {
      const c = s.previewCapabilities || {};
      caps.textContent = c.ffmpegAvailable
        ? 'ffmpeg found \u2014 hover clips enabled.'
        : 'ffmpeg not found \u2014 remote/catalogue thumbnails and tinted placeholders only, no hover clips.';
    }

    const aboutCacheDir = document.getElementById('about-cachedir');
    if (aboutCacheDir) aboutCacheDir.textContent = s.cacheDirectory || '(default)';
    const aboutFfmpeg = document.getElementById('about-ffmpeg');
    if (aboutFfmpeg) aboutFfmpeg.textContent = s.ffmpegPath || '(search PATH)';

    renderFolderList();
    renderSourceToggles();
    renderCacheUsage();
  }

  function renderCacheUsage() {
    const bar = document.getElementById('cache-bar');
    if (!bar || !Store.cacheStats || !Store.settings) return;
    const { pinnedBytes, rollingBytes, budgetBytes, freeDiskBytes } = Store.cacheStats;
    const budget = budgetBytes || 1;
    const pinnedPct = Math.min(100, (pinnedBytes / budget) * 100);
    const rollingPct = Math.min(100 - pinnedPct, (rollingBytes / budget) * 100);
    const freePct = Math.max(0, 100 - pinnedPct - rollingPct);
    bar.innerHTML =
      `<div class="seg-pinned" style="width:${pinnedPct}%"></div>` +
      `<div class="seg-rolling" style="width:${rollingPct}%"></div>` +
      `<div class="seg-free" style="width:${freePct}%"></div>`;

    const legendFree = document.getElementById('cache-legend-free');
    if (legendFree) {
      legendFree.textContent =
        `${formatBytes(pinnedBytes)} kept + ${formatBytes(rollingBytes)} rolling of ${formatBytes(budgetBytes)} \u00b7 ` +
        `${formatBytes(freeDiskBytes)} free on disk`;
    }
  }

  // ======================================================================================
  // 13. Toast
  // ======================================================================================

  function showToast(message, actions) {
    const host = document.getElementById('toast-host');
    const el = document.createElement('div');
    el.className = 'toast';
    el.innerHTML = '<div class="toast-msg"></div><div class="toast-actions"></div>';
    el.querySelector('.toast-msg').textContent = message;
    const actionsEl = el.querySelector('.toast-actions');
    for (const action of actions || []) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'btn';
      btn.textContent = action.label;
      btn.addEventListener('click', () => { action.onClick(); el.remove(); });
      actionsEl.appendChild(btn);
    }
    host.appendChild(el);
    setTimeout(() => { if (el.parentElement) el.remove(); }, 15000);
  }

  // ======================================================================================
  // 14. Host -> page message handlers
  // ======================================================================================

  function pruneRemovedClips() {
    for (const key of Array.from(Store.state.keys())) if (!Store.clips.has(key)) Store.state.delete(key);
    for (const key of Array.from(Store.preview.keys())) if (!Store.clips.has(key)) Store.preview.delete(key);
    for (const key of Array.from(Store.selection)) if (!Store.clips.has(key)) Store.selection.delete(key);
    for (const key of Array.from(Store.requestedStill)) if (!Store.clips.has(key)) Store.requestedStill.delete(key);
  }

  function registerHostHandlers() {
    Bridge.on('catalog', (msg) => {
      Store.clips.clear();
      Store.order = [];
      for (const clip of msg.clips) { Store.clips.set(clip.key, clip); Store.order.push(clip.key); }
      pruneRemovedClips();
      showRefreshSpinner(false);
      if (Store.detailKey && !Store.clips.has(Store.detailKey)) closeDetail();
      onFiltersChanged();
      if (settingsBuilt) renderSourceToggles();
    });

    Bridge.on('stateDelta', (msg) => {
      let flagsChanged = false;
      for (const [key, patch] of Object.entries(msg.clips)) {
        const prev = Store.state.get(key) || {};
        const next = Object.assign({}, prev, patch);
        Store.state.set(key, next);
        if (prev.inRotation !== next.inRotation || prev.pinned !== next.pinned ||
            prev.cached !== next.cached || prev.health !== next.health || prev.isNew !== next.isNew) {
          flagsChanged = true;
        }
        updateCardVisuals(key);
        if (Store.detailKey === key) {
          document.getElementById('toggle-rotation').checked = !!next.inRotation;
          document.getElementById('toggle-pin').checked = !!next.pinned;
          renderDetailDownload(next);
          renderDetailTags(key);
        }
      }
      if (flagsChanged) {
        recomputeVisible();
        buildGrid();
        renderFacets();
        applySelectionChanged();
      }
    });

    Bridge.on('previewReady', (msg) => {
      const pv = Store.preview.get(msg.key) || {};
      pv[msg.kind.toLowerCase()] = {
        status: msg.status,
        url: msg.status === 'Ready' ? msg.url : null,
        color: msg.placeholderColor,
      };
      Store.preview.set(msg.key, pv);
      updateCardThumb(msg.key);
      if (Store.detailKey === msg.key) updateDetailPreview();
      if (msg.kind === 'Hover' && hoverKey === msg.key && msg.status === 'Ready' && msg.url) {
        const cardEl = cardEls.get(msg.key);
        if (cardEl) showHoverVideo(cardEl, msg.url);
      }
    });

    Bridge.on('settings', (msg) => {
      Store.settings = msg.values;
      if (settingsBuilt) syncSettingsValues();
      else if (!document.getElementById('settings-overlay').classList.contains('hidden')) buildSettingsPanes();
      renderCacheUsage();
    });

    Bridge.on('cacheStats', (msg) => {
      Store.cacheStats = msg;
      renderCacheUsage();
    });

    Bridge.on('pinRefused', (msg) => {
      showToast(
        `Keeping ${msg.keys.length} clip(s) offline needs ${msg.requiredGb} GB, over the ${msg.budgetGb} GB limit.`,
        [
          { label: `Raise limit to ${msg.requiredGb} GB`, onClick: () => {
            Bridge.send('saveSettings', { patch: { cacheBudgetGb: msg.requiredGb } });
            Bridge.send('setPinned', { keys: msg.keys, value: true });
          } },
          { label: 'Cancel', onClick: () => {} },
        ],
      );
    });

    // Shape not finalized per the contract; just keep the spinner alive until `catalog` lands.
    Bridge.on('refreshProgress', () => showRefreshSpinner(true));
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
    window.addEventListener('resize', () => { if (Store.detailKey) sendPanelRect(); });
    document.getElementById('detail').addEventListener('scroll', () => { if (Store.detailKey) sendPanelRect(); });
  }

  function wireGlobalKeys() {
    document.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      if (!document.getElementById('settings-overlay').classList.contains('hidden')) { closeSettings(); return; }
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
  }

  document.addEventListener('DOMContentLoaded', init);
})();
