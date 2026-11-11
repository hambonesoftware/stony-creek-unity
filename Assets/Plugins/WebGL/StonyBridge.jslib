mergeInto(LibraryManager.library, {
  // Latest explorer state, read by the page and the browser tests.
  StonyReport: function (ptr) {
    try { window.stonyState = JSON.parse(UTF8ToString(ptr)); } catch (e) { }
  },
  // The page's query string, e.g. "?q=0" for the light test quality.
  StonyQuery: function () {
    var s = window.location.search || "";
    var n = lengthBytesUTF8(s) + 1, b = _malloc(n);
    stringToUTF8(s, b, n);
    return b;
  },
  // Canvas pixels per CSS pixel, so touch targets keep a physical size.
  StonyPixelRatio: function () {
    var c = document.getElementById('unity-canvas');
    if (c && c.clientWidth > 0) return c.width / c.clientWidth;
    return window.devicePixelRatio || 1;
  }
});
