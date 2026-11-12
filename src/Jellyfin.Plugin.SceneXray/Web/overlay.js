/*
 * X-Ray overlay for jellyfin-web.
 *
 * How it knows what is playing: jellyfin-web reports playback to /Sessions/Playing{,/Progress,/Stopped}
 * (through fetch or XHR depending on the code path). We read those request bodies for the item id and
 * position, and use the <video> element's clock between reports. Nothing in Jellyfin is modified.
 */
(function () {
  'use strict';
  if (window.__sceneXray) return;
  window.__sceneXray = true;

  // ---------------------------------------------------------------- setup
  var script = document.currentScript;
  var BASE = script && script.src ? script.src.replace(/SceneXray\/overlay\.js.*$/, '') : '/';
  var POLL_MS = 400;

  var state = {
    itemId: null,
    timeline: null,
    status: null,
    offset: 0,          // media position minus <video>.currentTime (non-zero while transcoding from a seek)
    lastPos: 0,
    lastSeen: 0,         // last time a <video> was on screen
    sceneKey: '',
    panelOpen: false,
    tab: 'scene',
    detail: null,        // personId shown in detail view
    pausedByUs: false,
    isAdmin: null,
    cast: null,          // Jellyfin People for admin "assign" lists
    statusTimer: 0,
    lastFocus: null,
    onScreenOnly: stored('sxr-on-screen-only') === '1', // hide people in the scene but off camera
  };

  function stored(key) { try { return localStorage.getItem(key); } catch (e) { return null; } }
  function store(key, v) { try { localStorage.setItem(key, v); } catch (e) { /* private mode */ } }

  var link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = BASE + 'SceneXray/overlay.css' + (script && script.src.indexOf('?') > 0 ? script.src.slice(script.src.indexOf('?')) : '');
  document.head.appendChild(link);

  // ---------------------------------------------------------------- API
  function client() { return window.ApiClient; }

  function token() {
    var c = client();
    try { if (c && c.accessToken && c.accessToken()) return c.accessToken(); } catch (e) { /* ignore */ }
    try {
      var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
      var s = (creds.Servers || [])[0];
      return s && s.AccessToken;
    } catch (e) { return null; }
  }

  function userId() {
    var c = client();
    try { return c && c.getCurrentUserId && c.getCurrentUserId(); } catch (e) { return null; }
  }

  /** Jellyfin serialises JSON in PascalCase; the overlay works in camelCase. */
  function camel(v) {
    if (Array.isArray(v)) return v.map(camel);
    if (!v || typeof v !== 'object') return v;
    var o = {};
    Object.keys(v).forEach(function (k) { o[k.charAt(0).toLowerCase() + k.slice(1)] = camel(v[k]); });
    return o;
  }

  function api(path, opts) {
    opts = opts || {};
    var headers = { Accept: 'application/json' };
    var t = token();
    if (t) headers.Authorization = 'MediaBrowser Token="' + t + '"';
    return origFetch.call(window, BASE + path, { method: opts.method || 'GET', headers: headers })
      .then(function (r) {
        var mine = path.indexOf('SceneXray/') === 0;
        if (r.status === 404 && opts.allow404) return r.json().then(function (b) { return { notFound: true, body: mine ? camel(b) : b }; });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        var type = r.headers.get('content-type') || '';
        return type.indexOf('json') >= 0 ? r.json().then(function (b) { return mine ? camel(b) : b; }) : null;
      });
  }

  function img(id, h) { return BASE + 'Items/' + id + '/Images/Primary?fillHeight=' + h + '&quality=90'; }

  /** Admin-only images need the auth header, so load them as blobs. */
  function authBackground(el, path) {
    var headers = {};
    var t = token();
    if (t) headers.Authorization = 'MediaBrowser Token="' + t + '"';
    origFetch.call(window, BASE + path, { headers: headers })
      .then(function (r) { return r.ok ? r.blob() : null; })
      .then(function (b) { if (b) el.style.backgroundImage = 'url("' + URL.createObjectURL(b) + '")'; })
      .catch(function () { /* leave the placeholder */ });
    return el;
  }

  // ---------------------------------------------------------------- playback detection
  var origFetch = window.fetch;
  window.fetch = function (input, init) {
    try {
      var url = typeof input === 'string' ? input : (input && input.url);
      if (url && url.indexOf('Sessions/Playing') !== -1 && init && init.body) onReport(String(url), init.body);
    } catch (e) { /* never break Jellyfin */ }
    return origFetch.apply(this, arguments);
  };

  var xOpen = XMLHttpRequest.prototype.open;
  var xSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url) {
    this.__sxrUrl = url;
    return xOpen.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function (body) {
    try {
      if (this.__sxrUrl && String(this.__sxrUrl).indexOf('Sessions/Playing') !== -1) onReport(String(this.__sxrUrl), body);
    } catch (e) { /* never break Jellyfin */ }
    return xSend.apply(this, arguments);
  };

  function onReport(url, body) {
    if (!/\/Sessions\/Playing(\/Progress|\/Stopped)?(\?|$)/i.test(url)) return;
    var d = typeof body === 'string' ? JSON.parse(body) : body;
    if (!d) return;
    var id = d.ItemId || d.itemId;
    var ticks = d.PositionTicks != null ? d.PositionTicks : d.positionTicks;
    if (/Stopped/i.test(url)) { if (!id || norm(id) === state.itemId) reset(); return; }
    if (!id) return;
    setItem(norm(id));
    if (ticks != null) calibrate(ticks / 1e7);
  }

  function norm(id) { return String(id).replace(/-/g, '').toLowerCase(); }

  /**
   * Jellyfin keeps earlier pages in the DOM (hidden), so only count elements actually rendered.
   * getClientRects() is empty when the element or an ancestor is display:none; offsetParent
   * would not do, since it is always null for position:fixed elements like the player controls.
   */
  function rendered(el) {
    return !!el && el.getClientRects().length > 0 && !el.closest('.hide');
  }

  function video() {
    var all = document.querySelectorAll('video.htmlvideoplayer, .videoPlayerContainer video');
    for (var i = 0; i < all.length; i++) if (rendered(all[i]) || !all[i].paused) return all[i];
    return all[0] || null;
  }

  function calibrate(pos) {
    var v = video();
    state.lastPos = pos;
    if (v && !isNaN(v.currentTime)) {
      var off = pos - v.currentTime;
      state.offset = Math.abs(off) < 1.5 ? 0 : off; // small differences are report latency, not an offset
    }
  }

  function position() {
    var v = video();
    if (v && !isNaN(v.currentTime)) return v.currentTime + state.offset;
    return state.lastPos;
  }

  function canSeek(t) {
    var v = video();
    return !!v && state.offset === 0 && isFinite(v.duration) && t >= 0 && t <= v.duration;
  }

  function seek(t) {
    var v = video();
    if (canSeek(t)) v.currentTime = Math.max(0, t - 1);
  }

  function osdVisible() {
    var all = document.querySelectorAll('.videoOsdBottom');
    for (var i = 0; i < all.length; i++) {
      var osd = all[i];
      if (rendered(osd) && !osd.classList.contains('videoOsdBottom-hidden')) return true;
    }
    return false;
  }

  // ---------------------------------------------------------------- data
  function setItem(id) {
    if (id === state.itemId) return;
    reset();
    state.itemId = id;
    load(id);
  }

  function reset() {
    clearTimeout(state.statusTimer);
    closePanel(true);
    state.itemId = null;
    state.timeline = null;
    state.status = null;
    state.cast = null;
    state.offset = 0;
    state.sceneKey = '';
    state.detail = null;
    render();
  }

  function load(id) {
    api('SceneXray/Items/' + id + '/Timeline', { allow404: true }).then(function (r) {
      if (id !== state.itemId) return;
      if (r && r.notFound) {
        state.status = r.body;
        if (r.body && (r.body.state === 'queued' || r.body.state === 'running')) {
          state.statusTimer = setTimeout(function () { load(id); }, 10000);
        }
      } else {
        state.timeline = r;
        state.status = null;
      }
      state.sceneKey = '';
      render();
    }).catch(function () { /* plugin unreachable: stay invisible */ });

    if (state.isAdmin === null && userId()) {
      api('Users/' + userId()).then(function (u) {
        state.isAdmin = !!(u && u.Policy && u.Policy.IsAdministrator);
      }).catch(function () { state.isAdmin = false; });
    }
  }

  function covers(intervals, t) {
    return (intervals || []).some(function (i) { return t >= i[0] && t <= i[1]; });
  }

  /**
   * Who is in the scene at t: people whose face is on screen first, then the rest of the scene
   * (cutaways, off-camera voices), each in screen-time order. onScreen holds the ids on screen.
   */
  function inScene(t) {
    var tl = state.timeline;
    if (!tl) return { people: [], music: [], onScreen: {} };
    var on = [], off = [], onScreen = {};
    tl.people.forEach(function (p) {
      if (!covers(p.intervals, t)) return;
      if (covers(p.spans, t)) { on.push(p); onScreen[p.personId] = true; } else off.push(p);
    });
    return {
      people: state.onScreenOnly ? on : on.concat(off),
      music: tl.music.filter(function (m) { return t >= m.start && t <= m.end; }),
      onScreen: onScreen,
      offCount: off.length,
    };
  }

  function person(id) {
    return state.timeline && state.timeline.people.filter(function (p) { return p.personId === id; })[0];
  }

  // ---------------------------------------------------------------- DOM helpers
  function h(tag, attrs) {
    var el = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (k) {
        var v = attrs[k];
        if (v == null || v === false) return;
        if (k === 'class') el.className = v;
        else if (k === 'text') el.textContent = v;
        else if (k.slice(0, 2) === 'on') el.addEventListener(k.slice(2), v);
        else el.setAttribute(k, v === true ? '' : v);
      });
    }
    for (var i = 2; i < arguments.length; i++) {
      var c = arguments[i];
      if (c == null || c === false) continue;
      (Array.isArray(c) ? c : [c]).forEach(function (x) {
        if (x != null && x !== false) el.appendChild(typeof x === 'string' ? document.createTextNode(x) : x);
      });
    }
    return el;
  }

  function initials(name) {
    return (name || '?').split(/\s+/).filter(Boolean).slice(0, 2).map(function (w) { return w[0]; }).join('').toUpperCase();
  }

  function avatar(p, size) {
    var a = h('span', { class: 'sxr-ava', 'aria-hidden': 'true' }, initials(p.name));
    var i = new Image();
    i.onload = function () { a.textContent = ''; a.style.backgroundImage = 'url("' + i.src + '")'; };
    i.src = img(p.personId, size);
    return a;
  }

  function fmtTime(s) {
    s = Math.max(0, Math.floor(s));
    var hh = Math.floor(s / 3600), mm = Math.floor((s % 3600) / 60), ss = s % 60;
    return (hh ? hh + ':' + String(mm).padStart(2, '0') : String(mm)) + ':' + String(ss).padStart(2, '0');
  }

  function fmtDuration(s) {
    if (s < 60) return Math.round(s) + ' sec';
    var m = Math.round(s / 60);
    return m < 60 ? m + ' min' : Math.floor(m / 60) + ' h ' + (m % 60) + ' min';
  }

  /** "Grace" for on-screen roles, "Voice of Buddy" for voice credits (cartoons, costumes). */
  function roleText(p) {
    if (p.voiceRole) return p.role ? 'Voice of ' + p.role : 'Voice';
    return p.role || '';
  }

  function personRow(p, onclick, offScreen) {
    var role = roleText(p);
    return h('button', { class: offScreen ? 'sxr-row sxr-off' : 'sxr-row', type: 'button', onclick: onclick },
      avatar(p, 160),
      h('span', { class: 'sxr-txt' },
        h('span', { class: 'sxr-name', text: p.name }),
        role ? h('span', { class: 'sxr-sub', text: role }) : null));
  }

  function castMember(id) {
    return state.timeline && (state.timeline.cast || []).filter(function (c) { return c.personId === id; })[0];
  }

  function musicRow(m, onclick, showTime) {
    var sub = [m.artist, showTime ? fmtTime(m.start) : null].filter(Boolean).join(' · ');
    return h('button', { class: 'sxr-row', type: 'button', onclick: onclick },
      h('span', { class: 'sxr-ava sxr-note', 'aria-hidden': 'true' }, '♪'),
      h('span', { class: 'sxr-txt' },
        h('span', { class: 'sxr-name', text: m.title }),
        sub ? h('span', { class: 'sxr-sub', text: sub }) : null));
  }

  // ---------------------------------------------------------------- UI
  var root = h('div', { class: 'sxr-root' });
  var peek = h('div', { class: 'sxr-peek', role: 'region', 'aria-label': 'X-Ray' });
  var panel = h('aside', { class: 'sxr-panel', role: 'dialog', 'aria-label': 'X-Ray', 'aria-modal': 'false' });
  root.appendChild(peek);
  root.appendChild(panel);
  function mount() { if (!root.isConnected && document.body) document.body.appendChild(root); }

  function openPanel(tab, personId) {
    var v = video();
    state.tab = tab || state.tab || 'scene';
    state.detail = personId || null;
    if (!state.panelOpen) {
      state.lastFocus = document.activeElement;
      if (v && !v.paused) { v.pause(); state.pausedByUs = true; }
    }
    state.panelOpen = true;
    renderPanel();
    render();
    var first = panel.querySelector('[aria-selected="true"]') || panel.querySelector('button');
    if (first) first.focus({ preventScroll: true });
  }

  function closePanel(silent) {
    if (!state.panelOpen) return;
    state.panelOpen = false;
    state.detail = null;
    var v = video();
    if (!silent && state.pausedByUs && v && v.paused) v.play();
    state.pausedByUs = false;
    render();
    if (!silent && state.lastFocus && state.lastFocus.focus) state.lastFocus.focus({ preventScroll: true });
  }

  function render() {
    mount();
    var active = !!state.itemId && Date.now() - state.lastSeen < 2000;
    var v = video();
    var showPeek = active && !state.panelOpen && (osdVisible() || (v && v.paused)) && (state.timeline || state.status);
    peek.classList.toggle('sxr-show', !!showPeek);
    panel.classList.toggle('sxr-show', active && state.panelOpen);
    if (!active && state.panelOpen) closePanel(true);
  }

  function renderPeek(now) {
    peek.textContent = '';
    if (!state.timeline) {
      var st = state.status;
      if (st && (st.state === 'queued' || st.state === 'running')) {
        peek.appendChild(h('button', { class: 'sxr-peek-head', type: 'button', onclick: function () { openPanel('scene'); } },
          h('b', { text: 'X-Ray' }), st.state === 'running' ? 'preparing ' + Math.round(st.progress) + '%' : 'queued'));
      }
      return;
    }

    var shown = now.people.slice(0, 4);
    var more = now.people.length - shown.length;
    peek.appendChild(h('button', { class: 'sxr-peek-head', type: 'button', 'aria-label': 'Open X-Ray (X)', onclick: function () { openPanel('scene'); } },
      h('b', { text: 'X-Ray' }), now.people.length || now.music.length ? 'In scene' : 'Cast & music'));
    var list = h('div', { class: 'sxr-peek-list' });
    shown.forEach(function (p) { list.appendChild(personRow(p, function () { openPanel('scene', p.personId); }, !now.onScreen[p.personId])); });
    if (more > 0) list.appendChild(h('button', { class: 'sxr-peek-head', type: 'button', onclick: function () { openPanel('scene'); } }, '+ ' + more + ' more'));
    now.music.slice(0, 1).forEach(function (m) { list.appendChild(musicRow(m, function () { openPanel('music'); }, false)); });
    peek.appendChild(list);
  }

  function renderPanel() {
    panel.textContent = '';
    var tl = state.timeline;
    var top = h('div', { class: 'sxr-top' },
      h('span', { class: 'sxr-title' }, h('b', { text: 'X-Ray' })),
      h('button', { class: 'sxr-icon-btn', type: 'button', 'aria-label': 'Close X-Ray', onclick: function () { closePanel(); } }, '✕'));
    panel.appendChild(top);

    var tabs = [['scene', 'In scene'], ['cast', 'Cast'], ['music', 'Music']];
    var tabBar = h('div', { class: 'sxr-tabs', role: 'tablist' }, tabs.map(function (t) {
      return h('button', {
        class: 'sxr-tab', type: 'button', role: 'tab', 'aria-selected': String(state.tab === t[0] && !state.detail),
        onclick: function () { state.tab = t[0]; state.detail = null; renderPanel(); },
      }, t[1]);
    }));
    panel.appendChild(tabBar);

    var body = h('div', { class: 'sxr-body' });
    panel.appendChild(body);

    if (!tl) {
      var st = state.status || {};
      var msg = st.state === 'running' ? 'X-Ray is analysing this title (' + Math.round(st.progress) + '%). It will appear here when ready.'
        : st.state === 'queued' ? 'This title is queued for X-Ray analysis' + (st.position ? ' (' + st.position + ' ahead of it).' : '.')
        : st.state === 'failed' ? 'X-Ray analysis failed for this title.'
        : 'X-Ray has not analysed this title yet.';
      body.appendChild(h('p', { class: 'sxr-empty', text: msg }));
      if (st.state === 'running') body.appendChild(h('div', { class: 'sxr-bar' }, h('i', { style: 'width:' + Math.round(st.progress) + '%' })));
      return;
    }

    if (state.detail) { renderDetail(body, person(state.detail) || castMember(state.detail)); return; }

    var now = inScene(position());
    if (state.tab === 'scene') {
      body.appendChild(h('label', { class: 'sxr-toggle' },
        h('input', { type: 'checkbox', checked: state.onScreenOnly, onchange: function (e) {
          state.onScreenOnly = e.target.checked;
          store('sxr-on-screen-only', state.onScreenOnly ? '1' : '0');
          state.sceneKey = '';
          renderPanel();
        } }),
        'On screen only'));
      if (!now.people.length && !now.music.length) {
        body.appendChild(h('p', { class: 'sxr-empty', text: state.onScreenOnly && now.offCount
          ? 'No one X-Ray recognises is on screen. ' + now.offCount + ' more in this scene.'
          : 'No one X-Ray recognises is in this scene.' }));
      }
      var split = !state.onScreenOnly && now.people.some(function (p) { return now.onScreen[p.personId]; });
      now.people.forEach(function (p, i) {
        var off = !now.onScreen[p.personId];
        if (split && off && (i === 0 || now.onScreen[now.people[i - 1].personId])) {
          body.appendChild(h('div', { class: 'sxr-section', text: 'Also in this scene' }));
        }
        body.appendChild(personRow(p, function () { state.detail = p.personId; renderPanel(); }, off));
      });
      if (now.music.length) body.appendChild(h('div', { class: 'sxr-section', text: 'Playing' }));
      now.music.forEach(function (m) { body.appendChild(musicRow(m, null, false)); });
    } else if (state.tab === 'cast') {
      if (!tl.people.length) body.appendChild(h('p', { class: 'sxr-empty', text: 'No cast members were recognised in this title.' }));
      tl.people.forEach(function (p) {
        var row = personRow(p, function () { state.detail = p.personId; renderPanel(); });
        row.querySelector('.sxr-txt').appendChild(h('span', { class: 'sxr-sub', text: (p.source === 'voice' ? 'Speaks for ' : 'On screen ') + fmtDuration(p.screenTimeSeconds) }));
        body.appendChild(row);
      });
      var found = {};
      tl.people.forEach(function (p) { found[p.personId] = 1; });
      var rest = (tl.cast || []).filter(function (c) { return !found[c.personId]; });
      if (rest.length) {
        body.appendChild(h('div', { class: 'sxr-section', text: tl.people.length ? 'Also in the cast' : 'Cast' }));
        rest.forEach(function (c) { body.appendChild(personRow(c, function () { state.detail = c.personId; renderPanel(); })); });
      }
      if (state.isAdmin && tl.unknown && tl.unknown.length) renderUnknown(body, tl);
    } else {
      if (!tl.music.length) {
        body.appendChild(h('p', { class: 'sxr-empty', text: tl.musicAnalyzed
          ? 'No songs were identified. Songs in your Jellyfin music library are matched automatically; an AudD token in the plugin settings identifies the rest.'
          : 'Music has not been analysed for this title.' }));
      }
      tl.music.forEach(function (m) {
        body.appendChild(musicRow(m, canSeek(m.start) ? function () { seek(m.start); closePanel(); } : null, true));
      });
    }
  }

  function renderDetail(body, p) {
    if (!p) { state.detail = null; renderPanel(); return; }
    body.appendChild(h('button', { class: 'sxr-back', type: 'button', onclick: function () { state.detail = null; renderPanel(); } }, '‹ Back'));
    body.appendChild(h('div', { class: 'sxr-hero' }, avatar(p, 400),
      h('div', null,
        h('h2', { text: p.name }),
        roleText(p) ? h('div', { class: 'sxr-sub', text: p.voiceRole ? roleText(p) : 'as ' + p.role }) : null,
        p.screenTimeSeconds ? h('div', { class: 'sxr-sub', text: (p.source === 'voice' ? 'Speaks for ' : 'On screen ') + fmtDuration(p.screenTimeSeconds) }) : null)));

    if (p.intervals && p.intervals.length) body.appendChild(h('div', { class: 'sxr-section', text: 'Scenes' }));
    if (p.intervals && p.intervals.length) body.appendChild(h('div', { class: 'sxr-chips' }, p.intervals.slice(0, 40).map(function (i) {
      var ok = canSeek(i[0]);
      return h('button', { class: 'sxr-chip', type: 'button', disabled: !ok, title: ok ? 'Jump to this scene' : null,
        onclick: ok ? function () { seek(i[0]); closePanel(); } : null }, fmtTime(i[0]));
    })));

    var lib = h('div', { class: 'sxr-posters' });
    body.appendChild(h('div', { class: 'sxr-section', text: 'Also in your library' }));
    body.appendChild(lib);
    var uid = userId();
    if (uid) {
      api('Users/' + uid + '/Items?PersonIds=' + p.personId + '&Recursive=true&IncludeItemTypes=Movie,Series&SortBy=PremiereDate&SortOrder=Descending&Limit=12')
        .then(function (r) {
          var items = (r && r.Items || []).filter(function (x) { return norm(x.Id) !== state.itemId; });
          if (!items.length) lib.replaceWith(h('p', { class: 'sxr-empty', text: 'Nothing else with ' + p.name + '.' }));
          items.forEach(function (x) {
            lib.appendChild(h('button', { class: 'sxr-poster', type: 'button', title: 'Open ' + x.Name + ' in a new tab',
              onclick: function () { window.open('#/details?id=' + x.Id + (x.ServerId ? '&serverId=' + x.ServerId : ''), '_blank', 'noopener'); } },
              h('i', { style: 'background-image:url("' + img(x.Id, 300) + '")' }),
              h('span', { text: x.Name })));
          });
        }).catch(function () { lib.remove(); });
    }

    if (state.isAdmin && person(p.personId)) {
      body.appendChild(h('div', { class: 'sxr-admin' },
        h('button', { class: 'sxr-btn sxr-danger', type: 'button', onclick: function () {
          if (!confirm('Remove ' + p.name + ' from X-Ray for this title?')) return;
          api('SceneXray/Items/' + state.itemId + '/People/' + p.personId, { method: 'DELETE' })
            .then(function (t) { state.timeline = t; state.detail = null; state.sceneKey = ''; renderPanel(); });
        } }, 'Not ' + p.name.split(' ')[0] + '? Remove')));
    }
  }

  function renderUnknown(body, tl) {
    body.appendChild(h('div', { class: 'sxr-section', text: 'Not recognised (admin)' }));
    var grid = h('div', { class: 'sxr-unknowns' });
    body.appendChild(grid);
    tl.unknown.forEach(function (u) {
      grid.appendChild(h('button', { class: 'sxr-unknown', type: 'button', title: 'Name this person',
        onclick: function () { assignUnknown(body, u); } },
        authBackground(h('i'), u.thumbUrl),
        fmtDuration(u.screenTimeSeconds)));
    });
  }

  function assignUnknown(body, u) {
    var uid = userId();
    var castReady = state.cast ? Promise.resolve(state.cast) : api('Users/' + uid + '/Items/' + state.itemId).then(function (item) {
      var people = item.People || [];
      return item.SeriesId ? api('Users/' + uid + '/Items/' + item.SeriesId).then(function (s) { return people.concat(s.People || []); }) : people;
    }).then(function (people) {
      var seen = {};
      state.cast = people.filter(function (p) { return (p.Type === 'Actor' || p.Type === 'GuestStar') && !seen[p.Id] && (seen[p.Id] = 1); });
      return state.cast;
    });
    castReady.then(function (cast) {
      body.textContent = '';
      body.appendChild(h('button', { class: 'sxr-back', type: 'button', onclick: function () { renderPanel(); } }, '‹ Back'));
      body.appendChild(h('div', { class: 'sxr-hero' },
        authBackground(h('span', { class: 'sxr-ava' }), u.thumbUrl),
        h('div', null, h('h2', { text: 'Who is this?' }), h('div', { class: 'sxr-sub', text: 'On screen ' + fmtDuration(u.screenTimeSeconds) }))));
      var sel = h('select', { class: 'sxr-select', 'aria-label': 'Cast member' },
        h('option', { value: '', text: 'Choose a cast member…' }),
        cast.map(function (p) {
          return h('option', { value: p.Id, selected: norm(p.Id) === norm(u.suggestedPersonId || '') ? true : null, text: p.Name + (p.Role ? ' — ' + p.Role : '') });
        }));
      body.appendChild(sel);
      body.appendChild(h('div', { class: 'sxr-admin' }, h('button', { class: 'sxr-btn', type: 'button', onclick: function () {
        if (!sel.value) return;
        api('SceneXray/Items/' + state.itemId + '/Unknown/' + u.id + '/Assign?personId=' + sel.value, { method: 'POST' })
          .then(function (t) { state.timeline = t; state.sceneKey = ''; state.tab = 'cast'; renderPanel(); });
      } }, 'Save')));
      sel.focus();
    }).catch(function () { /* ignore */ });
  }

  // ---------------------------------------------------------------- loop & keys
  var lastError = '';
  setInterval(function () {
    try { tick(); } catch (e) {
      if (String(e) !== lastError) { lastError = String(e); console.error('[X-Ray] overlay error', e); }
    }
  }, POLL_MS);

  function tick() {
    if (video()) state.lastSeen = Date.now();
    if (!state.itemId) { render(); return; }
    var now = inScene(position());
    var key = (state.timeline ? 't' : 's' + JSON.stringify(state.status)) + '|' +
      now.people.map(function (p) { return p.personId + (now.onScreen[p.personId] ? '*' : ''); }).join(',') + '|' + now.music.map(function (m) { return m.title; }).join(',');
    if (key !== state.sceneKey) {
      state.sceneKey = key;
      renderPeek(now);
      if (state.panelOpen && state.tab === 'scene' && !state.detail) {
        // Keep keyboard focus at the same position across the re-render.
        var buttons = panel.querySelectorAll('button');
        var idx = Array.prototype.indexOf.call(buttons, document.activeElement);
        renderPanel();
        if (idx >= 0) {
          buttons = panel.querySelectorAll('button');
          if (buttons.length) buttons[Math.min(idx, buttons.length - 1)].focus({ preventScroll: true });
        }
      }
    }
    render();
  }

  document.addEventListener('keydown', function (e) {
    if (!state.itemId || !video()) return;
    var tag = (e.target && e.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.key === 'x' || e.key === 'X') {
      e.preventDefault();
      e.stopPropagation();
      if (state.panelOpen) closePanel(); else openPanel('scene');
    } else if (e.key === 'Escape' && state.panelOpen) {
      e.preventDefault();
      e.stopPropagation();
      if (state.detail) { state.detail = null; renderPanel(); } else closePanel();
    }
  }, true);
})();
