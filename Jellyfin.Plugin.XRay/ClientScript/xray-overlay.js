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
    onViewChange();
  }

  function onViewChange() {
    const href = location.href;
    const isPlayer =
      href.includes('videoosd') ||
      href.includes('nowplaying') ||
      !!document.querySelector('.videoOsdPage, #videoOsdPage');

    const m = href.match(/[?&]id=([a-f0-9-]{8,})/i);
    const itemId = m ? m[1] : null;

    if (isPlayer && itemId && itemId !== currentItemId) {
      currentItemId = itemId;
      teardown();
      waitForVideo(itemId);
    } else if (!isPlayer) {
      currentItemId = null;
      teardown();
    }
  }

  function waitForVideo(itemId, tries = 0) {
    const video = document.querySelector('video');
    if (video) {
      setup(video, itemId);
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
    lastKey  = '';
    castMeta = {};
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
        fetch(`/XRay/analyze/${itemId}`, { method: 'POST' }).catch(() => {});
        return;
      }
      if (!resp.ok) return;
      const data = await resp.json();
      renderActors(data.actors || []);
    } catch (_) { /* network / sidecar down */ }
  }

  // ------------------------------------------------------------------
  // Cast metadata
  // ------------------------------------------------------------------

  async function prefetchCast(itemId) {
    try {
      const server = window.ApiClient?.serverAddress() ?? '';
      const token  = window.ApiClient?._accessToken ?? '';
      if (!server) return;
      const r = await fetch(`${server}/Items/${itemId}?Fields=People&api_key=${token}`);
      if (!r.ok) return;
      const d = await r.json();
      (d.People || []).filter(p => p.Type === 'Actor').forEach(p => {
        castMeta[p.Name] = { role: p.Role || '', tag: p.PrimaryImageTag || '', id: p.Id || '' };
      });
    } catch (_) {}
  }

  // ------------------------------------------------------------------
  // Rendering
  // ------------------------------------------------------------------

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

    const server = window.ApiClient?.serverAddress() ?? '';

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
