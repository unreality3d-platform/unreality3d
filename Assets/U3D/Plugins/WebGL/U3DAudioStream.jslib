var U3DAudioStreamLibrary = {
  $U3DAudioStream: {
    players: {},
    nextId: 1,
    unlockEvents: ['pointerup', 'touchend', 'click', 'keydown'],

    tryPlay: function (p) {
      if (!p.wantsPlay) return;
      var result = p.el.play();
      if (result && result.catch) {
        result.catch(function (err) {
          if (!err) return;
          if (err.name === 'NotAllowedError') U3DAudioStream.armUnlock(p);
          else if (err.name !== 'AbortError') p.error = true;
        });
      }
    },

    armUnlock: function (p) {
      if (p.unlock) return;
      p.unlock = function () {
        U3DAudioStream.disarmUnlock(p);
        U3DAudioStream.tryPlay(p);
      };
      U3DAudioStream.unlockEvents.forEach(function (name) {
        document.addEventListener(name, p.unlock, true);
      });
    },

    disarmUnlock: function (p) {
      if (!p.unlock) return;
      var unlock = p.unlock;
      p.unlock = null;
      U3DAudioStream.unlockEvents.forEach(function (name) {
        document.removeEventListener(name, unlock, true);
      });
    },

    seek: function (p, seconds) {
      if (p.el.readyState >= 1) {
        try {
          p.el.currentTime = seconds;
          p.pendingSeek = -1;
        } catch (e) {
          p.pendingSeek = seconds;
        }
      } else {
        p.pendingSeek = seconds;
      }
    },

    stop: function (p) {
      p.wantsPlay = false;
      U3DAudioStream.disarmUnlock(p);
      p.el.pause();
      p.el.removeAttribute('src');
      p.el.load();
      p.ended = false;
      p.error = false;
      p.pendingSeek = -1;
    }
  },

  U3DAudioStream_Create: function () {
    var el = new Audio();
    el.preload = 'auto';
    var p = { el: el, ended: false, error: false, pendingSeek: -1, wantsPlay: false, unlock: null };
    el.addEventListener('ended', function () { p.ended = true; });
    el.addEventListener('error', function () { if (el.getAttribute('src')) p.error = true; });
    el.addEventListener('loadedmetadata', function () {
      if (p.pendingSeek >= 0) {
        try { el.currentTime = p.pendingSeek; } catch (e) {}
        p.pendingSeek = -1;
      }
    });
    var id = U3DAudioStream.nextId++;
    U3DAudioStream.players[id] = p;
    return id;
  },

  U3DAudioStream_Destroy: function (id) {
    var p = U3DAudioStream.players[id];
    if (!p) return;
    U3DAudioStream.stop(p);
    delete U3DAudioStream.players[id];
  },

  U3DAudioStream_Play: function (id, urlPtr, startSeconds, volume) {
    var p = U3DAudioStream.players[id];
    if (!p) return;
    var url = UTF8ToString(urlPtr);
    p.ended = false;
    p.error = false;
    p.wantsPlay = true;
    p.el.volume = Math.max(0, Math.min(1, volume));
    if (p.el.getAttribute('src') !== url) {
      p.pendingSeek = startSeconds > 0 ? startSeconds : -1;
      p.el.src = url;
    } else {
      U3DAudioStream.seek(p, Math.max(0, startSeconds));
    }
    U3DAudioStream.tryPlay(p);
  },

  U3DAudioStream_Pause: function (id) {
    var p = U3DAudioStream.players[id];
    if (!p) return;
    p.wantsPlay = false;
    U3DAudioStream.disarmUnlock(p);
    p.el.pause();
  },

  U3DAudioStream_Resume: function (id) {
    var p = U3DAudioStream.players[id];
    if (!p) return;
    p.wantsPlay = true;
    U3DAudioStream.tryPlay(p);
  },

  U3DAudioStream_Stop: function (id) {
    var p = U3DAudioStream.players[id];
    if (p) U3DAudioStream.stop(p);
  },

  U3DAudioStream_Seek: function (id, seconds) {
    var p = U3DAudioStream.players[id];
    if (p) U3DAudioStream.seek(p, seconds);
  },

  U3DAudioStream_SetVolume: function (id, volume) {
    var p = U3DAudioStream.players[id];
    if (p) p.el.volume = Math.max(0, Math.min(1, volume));
  },

  U3DAudioStream_GetTime: function (id) {
    var p = U3DAudioStream.players[id];
    if (!p) return 0;
    if (p.pendingSeek >= 0) return p.pendingSeek;
    return p.el.currentTime || 0;
  },

  U3DAudioStream_GetDuration: function (id) {
    var p = U3DAudioStream.players[id];
    if (!p) return 0;
    var d = p.el.duration;
    return isFinite(d) ? d : 0;
  },

  U3DAudioStream_IsPlaying: function (id) {
    var p = U3DAudioStream.players[id];
    return p && !p.el.paused && !p.el.ended ? 1 : 0;
  },

  U3DAudioStream_IsBuffering: function (id) {
    var p = U3DAudioStream.players[id];
    return p && !p.el.paused && p.el.readyState < 3 ? 1 : 0;
  },

  U3DAudioStream_ConsumeEnded: function (id) {
    var p = U3DAudioStream.players[id];
    if (!p || !p.ended) return 0;
    p.ended = false;
    return 1;
  },

  U3DAudioStream_HasError: function (id) {
    var p = U3DAudioStream.players[id];
    return p && p.error ? 1 : 0;
  }
};

autoAddDeps(U3DAudioStreamLibrary, '$U3DAudioStream');
mergeInto(LibraryManager.library, U3DAudioStreamLibrary);