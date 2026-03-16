/**
 * xray-overlay.js
 * Served by Jellyfin at /XRay/overlay.js and injected into the player page.
 * Polls /XRay/query on video.currentTime and renders actor cards.
 */
(function () {
  'use strict';

  const OVERLAY_ID = 'jellyfin-xray-root';
  const POLL_MS = 3000;
  const HIDE_DELAY_MS = 5000;

  let pollTimer = null;
  let lastKey = '';
  let hideTimer = null;
  let castMeta = {};
  let currentItemId = null;

  // ------------------------------------------------------------------
  // SPA router — watch for Jellyfin page transitions
  // ------------------------------------------------------------------

  function observeRouter() {
    document.addEventListener('viewshow', onViewChange);
    window.addEventListener('hashchange', onViewChange);
    const _push = history.pushState.bind(history);
    history.pushState = function (...a) { _push(...a); setTimeout(onViewChange, 150); };
    onViewChange();
  }

  function onViewChange() {
    const isPlayer =
      location.href.includes('videoosd') ||
      location.href.includes('nowplaying') ||
      !!document.querySelector('.videoOsdPage, #videoOsdPage');

    const idMatch = location.href.match(/[?&]id=([a-f0-9-]+)/i);
    const itemId = idMatch ? idMatch[1] : null;

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
    } else if (tries < 30) {
      setTimeout(() => waitForVideo(itemId, tries + 1), 300);
    }
  }

  // ------------------------------------------------------------------
  // Setup / teardown
  // ------------------------------------------------------------------

  function setup(video, itemId) {
    injectStyles();
    castMeta = {};
    lastKey = '';

    const playerEl = video.closest('[class*="player"], .videoPlayerContainer, .htmlVideoPlayer')
      || video.parentElement;
    if (getComputedStyle(playerEl).position === 'static') playerEl.style.position = 'relative';

    const root = document.createElement('div');
    root.id = OVERLAY_ID;
    root.innerHTML = `
      <div id="xray-panel">
        <div id="xray-cards"></div>
        <span id="xray-label">X-Ray</span>
      </div>`;
    playerEl.appendChild(root);

    prefetchCast(itemId);
    startPolling(video, itemId);

    video.addEventListener('pause', onPause);
    video.addEventListener('play', onPlay);
    video.addEventListener('emptied', teardown);
  }

  function teardown() {
    stopPolling();
    clearTimeout(hideTimer);
    const el = document.getElementById(OVERLAY_ID);
    if (el) el.remove();
    lastKey = '';
    castMeta = {};
  }

  // ------------------------------------------------------------------
  // Polling
  // ------------------------------------------------------------------

  function startPolling(video, itemId) {
    stopPolling();
    pollTimer = setInterval(() => {
      if (!video.paused) tick(video, itemId);
    }, POLL_MS);
  }

  function stopPolling() {
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  }

  async function tick(video, itemId) {
    const t = Math.floor(video.currentTime);
    try {
      const resp = await fetch(`/XRay/query?itemId=${itemId}&t=${t}`);
      if (resp.status === 404) {
        // No data yet — request analysis
        fetch(`/XRay/analyze/${itemId}`, { method: 'POST' }).catch(() => {});
        return;
      }
      if (!resp.ok) return;
      const data = await resp.json();
      renderActors(data.actors || []);
    } catch (_) { /* sidecar unavailable */ }
  }

  // ------------------------------------------------------------------
  // Cast metadata prefetch (for images + roles)
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
    const max = window._xrayMaxActors || 4;
    const visible = actors.slice(0, max);
    const key = visible.join(',');
    if (key === lastKey) return;
    lastKey = key;

    const panel  = document.getElementById('xray-panel');
    const cards  = document.getElementById('xray-cards');
    if (!panel || !cards) return;

    cards.innerHTML = '';

    if (visible.length === 0) {
      scheduleHide(panel);
      return;
    }

    cancelHide();
    panel.style.opacity = '1';

    visible.forEach((name, i) => {
      const meta = castMeta[name] || {};
      const card = document.createElement('div');
      card.className = 'xray-card';
      card.style.animationDelay = `${i * 60}ms`;

      const server = window.ApiClient?.serverAddress() ?? '';
      const thumbHtml = (meta.id && meta.tag && server)
        ? `<img class="xray-thumb" src="${server}/Items/${meta.id}/Images/Primary?tag=${meta.tag}&maxHeight=72" alt="${esc(name)}" onerror="this.style.display='none';this.nextSibling.style.display='flex'">`
        + `<div class="xray-initial" style="display:none">${initials(name)}</div>`
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

  function scheduleHide(panel) {
    if (hideTimer) return;
    hideTimer = setTimeout(() => { panel.style.opacity = '0'; hideTimer = null; }, HIDE_DELAY_MS);
  }

  function cancelHide() {
    if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
  }

  function onPause() { scheduleHide(document.getElementById('xray-panel')); }
  function onPlay()  { cancelHide(); }

  // ------------------------------------------------------------------
  // Styles (injected once)
  // ------------------------------------------------------------------

  function injectStyles() {
    if (document.getElementById('xray-styles')) return;
    const s = document.createElement('style');
    s.id = 'xray-styles';
    s.textContent = `
      #jellyfin-xray-root {
        position: absolute; top: 0; left: 0; right: 0; z-index: 10;
        pointer-events: none;
      }
      #xray-panel {
        display: flex; flex-wrap: wrap; gap: 8px;
        align-items: flex-start;
        padding: 12px 16px 20px;
        background: linear-gradient(180deg,rgba(0,0,0,.72) 0%,transparent 100%);
        min-height: 64px;
        transition: opacity .4s ease;
        opacity: 0;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        position: relative;
      }
      #xray-label {
        position: absolute; top: 14px; right: 16px;
        font-size: 10px; font-weight: 700; letter-spacing: 1.5px;
        color: rgba(255,255,255,.35); text-transform: uppercase;
        pointer-events: none;
      }
      #xray-cards { display: flex; flex-wrap: wrap; gap: 8px; pointer-events: auto; }
      .xray-card {
        display: flex; align-items: center; gap: 8px;
        background: rgba(255,255,255,.12);
        border: 1px solid rgba(255,255,255,.2);
        border-radius: 8px; padding: 5px 10px 5px 5px;
        backdrop-filter: blur(6px); -webkit-backdrop-filter: blur(6px);
        max-width: 190px;
        animation: xrayIn .3s ease both;
      }
      @keyframes xrayIn {
        from { opacity:0; transform:translateY(-5px); }
        to   { opacity:1; transform:translateY(0); }
      }
      .xray-card:hover .xray-role { max-height: 20px !important; opacity: 1 !important; }
      .xray-thumb-wrap { flex-shrink: 0; width: 36px; height: 36px; }
      .xray-thumb {
        width: 36px; height: 36px; border-radius: 50%; object-fit: cover;
        border: 1.5px solid rgba(255,255,255,.25);
        display: block;
      }
      .xray-initial {
        width: 36px; height: 36px; border-radius: 50%;
        background: rgba(255,255,255,.15); border: 1.5px solid rgba(255,255,255,.25);
        display: flex; align-items: center; justify-content: center;
        font-size: 12px; font-weight: 600; color: rgba(255,255,255,.75);
      }
      .xray-info { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
      .xray-name {
        font-size: 12px; font-weight: 600; color: #fff;
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
        text-shadow: 0 1px 3px rgba(0,0,0,.6);
      }
      .xray-role {
        font-size: 11px; color: rgba(255,255,255,.6);
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
        max-height: 0; opacity: 0;
        transition: max-height .2s, opacity .2s;
        text-shadow: 0 1px 3px rgba(0,0,0,.5);
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
      ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
  }

  // ------------------------------------------------------------------
  // Boot — wait for Jellyfin ApiClient then start watching routes
  // ------------------------------------------------------------------

  function boot(tries = 0) {
    if (window.ApiClient) {
      // Expose config hook so dashboard settings take effect live
      window._xrayMaxActors = 4; // overridden by configPage on load
      observeRouter();
    } else if (tries < 40) {
      setTimeout(() => boot(tries + 1), 250);
    }
  }

  boot();
})();
