/**
 * xray-overlay.js
 * Left-side X-Ray panel — slides in on mouse move, matches Amazon Prime X-Ray style.
 * Loaded once per session by visiting the X-Ray sidebar item.
 */
(function () {
  'use strict';

  if (window.__xrayLoaded) return;
  window.__xrayLoaded = true;

  const POLL_MS      = 3000;
  const IDLE_HIDE_MS = 4000;   // hide 4s after last mouse move (while playing)

  let pollTimer     = null;
  let idleTimer     = null;
  let lastKey       = '';
  let castMeta      = {};
  let currentItemId = null;
  let playerEl      = null;

  // ------------------------------------------------------------------
  // SPA router — detect player page
  // ------------------------------------------------------------------

  function observeRouter() {
    document.addEventListener('viewshow', onViewChange);
    window.addEventListener('hashchange', onViewChange);
    const _push = history.pushState.bind(history);
    history.pushState = function (...a) { _push(...a); setTimeout(onViewChange, 200); };

    // Fallback: watch for <video> element appearing in the DOM
    // This catches players that load without a URL change (e.g. modal players)
    new MutationObserver(() => {
      if (document.querySelector('video') && !currentItemId) {
        onViewChange();
      }
    }).observe(document.body, { childList: true, subtree: true });

    onViewChange();
  }

  function onViewChange() {
    const href = location.href;
    // Jellyfin 10.9 and earlier: #/videoosd, #/nowplaying
    // Jellyfin 10.10+: #/video (React router — no item ID in URL)
    const isPlayer =
      href.includes('videoosd') ||
      href.includes('nowplaying') ||
      /[#/]video[?&/]/.test(href) ||
      href.endsWith('#/video') ||
      !!document.querySelector('.videoOsdPage, #videoOsdPage, .osdContainer, [data-role="videoOsd"]');

    const m = href.match(/[?&]id=([a-f0-9-]{8,})/i);
    const urlItemId = m ? m[1].replace(/-/g, '') : null;

    if (isPlayer && urlItemId && urlItemId !== currentItemId) {
      currentItemId = urlItemId;
      teardown();
      waitForVideo(urlItemId);
    } else if (isPlayer && !urlItemId && !currentItemId) {
      // React router: item ID not in URL — extract from video poster attribute
      teardown();
      waitForVideo(null);
    } else if (!isPlayer) {
      currentItemId = null;
      teardown();
    }
  }

  // Extract Jellyfin item ID from a video element's poster URL
  // e.g. https://jellyfin.host/Items/534c46c51c7798800363975c382995b6/Images/Backdrop/...
  function extractItemIdFromVideo(video) {
    if (video && video.poster) {
      const m = video.poster.match(/\/Items\/([a-f0-9]{32,})\//i);
      if (m) return m[1];
    }
    return null;
  }

  function waitForVideo(itemId, tries = 0) {
    const video = document.querySelector('video');
    if (video) {
      const id = itemId || extractItemIdFromVideo(video);
      if (id) {
        currentItemId = id;
        setup(video, id);
      } else if (tries < 20) {
        // poster URL may not be populated yet
        setTimeout(() => waitForVideo(null, tries + 1), 300);
      }
    } else if (tries < 40) {
      setTimeout(() => waitForVideo(itemId, tries + 1), 300);
    }
  }

  // ------------------------------------------------------------------
  // Setup / teardown
  // ------------------------------------------------------------------

  function setup(video, itemId) {
    injectStyles();
    castMeta  = {};
    lastKey   = '';

    // Find the positioned container the video lives in
    playerEl = findContainer(video);

    // Build the panel DOM
    const root = document.createElement('div');
    root.id = 'xray-root';
    root.innerHTML = `
      <div id="xray-panel">
        <div id="xray-header"><span id="xray-logo">X-Ray</span></div>
        <div id="xray-cards"></div>
      </div>`;
    playerEl.appendChild(root);

    // Mouse watcher on the whole player
    playerEl.addEventListener('mousemove',  onMouseMove);
    playerEl.addEventListener('mouseleave', onMouseLeave);

    prefetchCast(itemId);
    startPolling(video, itemId);

    video.addEventListener('pause',   onPause);
    video.addEventListener('play',    onPlay);
    video.addEventListener('emptied', teardown);
  }

  function findContainer(video) {
    // Walk up until we hit something positioned (the player shell)
    let el = video.parentElement;
    while (el && el !== document.body) {
      const pos = getComputedStyle(el).position;
      if (pos === 'relative' || pos === 'absolute' || pos === 'fixed') return el;
      el = el.parentElement;
    }
    const fallback = video.closest('[class*="player"], .videoPlayerContainer, .htmlVideoPlayer')
                  || video.parentElement;
    if (getComputedStyle(fallback).position === 'static') fallback.style.position = 'relative';
    return fallback;
  }

  function teardown() {
    stopPolling();
    clearTimeout(idleTimer);
    const el = document.getElementById('xray-root');
    if (el) el.remove();
    if (playerEl) {
      playerEl.removeEventListener('mousemove',  onMouseMove);
      playerEl.removeEventListener('mouseleave', onMouseLeave);
      playerEl = null;
    }
    lastKey       = '';
    castMeta      = {};
    currentItemId = null;
  }

  // ------------------------------------------------------------------
  // Mouse / visibility
  // ------------------------------------------------------------------

  let _video = null; // saved for pause check

  function onMouseMove() {
    showPanel();
    resetIdleTimer();
  }

  function onMouseLeave() {
    if (_video && _video.paused) return; // keep visible when paused
    scheduleHide();
  }

  function showPanel() {
    const p = document.getElementById('xray-panel');
    if (p) p.classList.add('xray-visible');
  }

  function hidePanel() {
    const p = document.getElementById('xray-panel');
    if (p) p.classList.remove('xray-visible');
  }

  function resetIdleTimer() {
    clearTimeout(idleTimer);
    idleTimer = setTimeout(scheduleHide, IDLE_HIDE_MS);
  }

  function scheduleHide() {
    clearTimeout(idleTimer);
    hidePanel();
  }

  function onPause() { clearTimeout(idleTimer); showPanel(); }
  function onPlay()  { resetIdleTimer(); }

  // ------------------------------------------------------------------
  // Polling
  // ------------------------------------------------------------------

  function startPolling(video, itemId) {
    _video = video;
    stopPolling();
    pollTimer = setInterval(() => tick(video, itemId), POLL_MS);
  }

  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  }

  async function tick(video, itemId) {
    const t = Math.floor(video.currentTime);
    try {
      const resp = await fetch(`/XRay/query?itemId=${itemId}&t=${t}`);
      if (resp.status === 404) {
        triggerAnalysis(itemId);
        renderStatus('Analyzing… actor data will appear shortly.');
        return;
      }
      if (!resp.ok) return;
      const data = await resp.json();
      renderActors(data.actors || []);
    } catch (_) { /* network / sidecar down */ }
  }

  function triggerAnalysis(itemId) {
    const token = window.ApiClient?._accessToken ?? '';
    const auth  = token ? `?api_key=${token}` : '';
    fetch(`/XRay/analyze/${itemId}${auth}`, { method: 'POST' }).catch(() => {});
  }

  // ------------------------------------------------------------------
  // Cast metadata
  // ------------------------------------------------------------------

  function serverBase() {
    // serverAddress() may be empty in Jellyfin 10.11 — fall back to origin.
    try {
      const s = window.ApiClient?.serverAddress?.() ?? '';
      return s || window.location.origin;
    } catch (_) {
      return window.location.origin;
    }
  }

  async function prefetchCast(itemId) {
    try {
      const server = serverBase();
      const url    = `${server}/Items/${itemId}?Fields=People`;
      let d;
      // ApiClient.getJSON() injects the correct auth headers automatically.
      // Fall back to raw fetch + api_key param if getJSON is unavailable.
      if (window.ApiClient?.getJSON) {
        d = await window.ApiClient.getJSON(url);
      } else {
        const token = window.ApiClient?._accessToken ?? '';
        const r = await fetch(`${url}${token ? '&api_key=' + token : ''}`);
        if (!r.ok) { console.warn('[X-Ray] prefetchCast failed', r.status); return; }
        d = await r.json();
      }
      (d.People || []).filter(p => p.Type === 'Actor').forEach(p => {
        castMeta[p.Name] = { role: p.Role || '', tag: p.PrimaryImageTag || '', id: p.Id || '' };
      });
      console.log('[X-Ray] cast loaded:', Object.keys(castMeta).length, 'actors');
    } catch (e) { console.warn('[X-Ray] prefetchCast error', e); }
  }

  // ------------------------------------------------------------------
  // Rendering
  // ------------------------------------------------------------------

  function renderStatus(msg) {
    const key = '__status__' + msg;
    if (key === lastKey) return;
    lastKey = key;
    const cards = document.getElementById('xray-cards');
    if (!cards) return;
    cards.innerHTML = `<div class="xray-status">${esc(msg)}</div>`;
  }

  function renderActors(actors) {
    const max     = window._xrayMaxActors || 4;
    const visible = actors.slice(0, max);
    const key     = visible.join(',');
    if (key === lastKey) return;
    lastKey = key;

    const cards = document.getElementById('xray-cards');
    if (!cards) return;
    cards.innerHTML = '';

    if (visible.length === 0) return;

    const server = serverBase();

    visible.forEach((name, i) => {
      const meta = castMeta[name] || {};
      const card = document.createElement('div');
      card.className = 'xray-card';
      card.style.animationDelay = `${i * 55}ms`;

      const imgUrl = (meta.id && meta.tag && server)
        ? `${server}/Items/${meta.id}/Images/Primary?tag=${meta.tag}&maxWidth=96&maxHeight=128`
        : null;

      const thumbHtml = imgUrl
        ? `<img class="xray-thumb" src="${imgUrl}" alt="${esc(name)}"
               onerror="this.parentElement.innerHTML='<div class=xray-initial>${initials(name)}</div>'">`
        : `<div class="xray-initial">${initials(name)}</div>`;

      card.innerHTML = `
        <div class="xray-thumb-wrap">${thumbHtml}</div>
        <div class="xray-info">
          <div class="xray-name">${esc(name)}</div>
          ${meta.role ? `<div class="xray-role">${esc(meta.role)}</div>` : ''}
        </div>`;
      cards.appendChild(card);
    });
  }

  // ------------------------------------------------------------------
  // Styles
  // ------------------------------------------------------------------

  function injectStyles() {
    if (document.getElementById('xray-styles')) return;
    const s = document.createElement('style');
    s.id = 'xray-styles';
    s.textContent = `
      #xray-root {
        position: absolute;
        top: 0; left: 0; bottom: 0;
        z-index: 100;
        display: flex;
        align-items: center;
        pointer-events: none;
      }

      #xray-panel {
        pointer-events: auto;
        width: 210px;
        background: rgba(0,0,0,0.82);
        display: flex;
        flex-direction: column;
        transform: translateX(-100%);
        opacity: 0;
        transition: transform 0.22s ease, opacity 0.22s ease;
        max-height: 75vh;
        overflow-y: auto;
        overflow-x: hidden;
        scrollbar-width: none;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
        border-radius: 0 6px 6px 0;
      }
      #xray-panel::-webkit-scrollbar { display: none; }

      #xray-panel.xray-visible {
        transform: translateX(0);
        opacity: 1;
      }

      #xray-header {
        padding: 12px 14px 9px;
        border-bottom: 1px solid rgba(255,255,255,0.12);
        flex-shrink: 0;
      }

      #xray-logo {
        font-size: 12px;
        font-weight: 700;
        letter-spacing: 1px;
        color: rgba(255,255,255,0.9);
        text-transform: uppercase;
      }

      #xray-cards {
        display: flex;
        flex-direction: column;
        padding: 6px 0;
      }

      .xray-card {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 8px 14px;
        animation: xraySlide 0.22s ease both;
        transition: background 0.15s;
        cursor: default;
      }
      .xray-card:hover {
        background: rgba(255,255,255,0.07);
      }

      @keyframes xraySlide {
        from { opacity: 0; transform: translateX(-12px); }
        to   { opacity: 1; transform: translateX(0); }
      }

      .xray-thumb-wrap {
        flex-shrink: 0;
        width: 44px;
        height: 58px;
        border-radius: 3px;
        overflow: hidden;
        background: rgba(255,255,255,0.08);
      }

      .xray-thumb {
        width: 100%;
        height: 100%;
        object-fit: cover;
        object-position: top;
        display: block;
      }

      .xray-initial {
        width: 100%;
        height: 100%;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 15px;
        font-weight: 700;
        color: rgba(255,255,255,0.55);
        background: rgba(255,255,255,0.06);
      }

      .xray-info {
        flex: 1;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 3px;
      }

      .xray-name {
        font-size: 12px;
        font-weight: 600;
        color: #ffffff;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        line-height: 1.3;
      }

      .xray-role {
        font-size: 11px;
        color: rgba(255,255,255,0.52);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        line-height: 1.3;
      }

      .xray-status {
        padding: 10px 14px;
        font-size: 11px;
        color: rgba(255,255,255,0.45);
        line-height: 1.4;
      }
    `;
    document.head.appendChild(s);
  }

  // ------------------------------------------------------------------
  // Utilities
  // ------------------------------------------------------------------

  function initials(name) {
    return name.split(' ').map(w => w[0]).slice(0, 2).join('').toUpperCase();
  }

  function esc(str) {
    return str.replace(/[&<>"']/g, c =>
      ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'": '&#39;' }[c]));
  }

  // ------------------------------------------------------------------
  // Boot
  // ------------------------------------------------------------------

  function boot(tries = 0) {
    if (window.ApiClient) {
      window._xrayMaxActors = window._xrayMaxActors || 4;
      observeRouter();
      console.log('[X-Ray] overlay active');
    } else if (tries < 40) {
      setTimeout(() => boot(tries + 1), 250);
    }
  }

  boot();
})();
