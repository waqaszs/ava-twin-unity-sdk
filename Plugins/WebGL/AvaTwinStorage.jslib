mergeInto(LibraryManager.library, {
  // Flush persistentDataPath (IDBFS) to IndexedDB so cached avatars survive a
  // page reload. Coalesces calls: at most one sync runs, plus one queued.
  AvaTwin_SyncPersistentStorage: function () {
    if (typeof FS === "undefined" || !FS.syncfs) return;

    // State lives on this Unity instance's FS object: no window dependency (works in
    // a worker) and no sharing between two Unity instances on one page.
    if (!FS.__avaTwinFsSync) {
      FS.__avaTwinFsSync = { running: false, pending: false };
    }
    var state = FS.__avaTwinFsSync;
    if (state.running) {
      state.pending = true;
      return;
    }

    var run = function () {
      state.running = true;
      state.pending = false;
      FS.syncfs(false, function (err) {
        state.running = false;
        if (err) console.warn("[Ava-Twin] Avatar cache sync failed:", err);
        if (state.pending) run();
      });
    };
    run();
  }
});
