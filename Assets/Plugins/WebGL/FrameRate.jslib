// Потолок частоты кадров для WebGL (GameLoot.cs): Application.targetFrameRate там не работает, ограничение
// живёт в шаблоне страницы (WebGLTemplates/Full/index.html). 0 — без ограничения.
mergeInto(LibraryManager.library, {
  SetFrameCap: function (fps) {
    if (window.SetFrameCap) window.SetFrameCap(fps);
  }
});
