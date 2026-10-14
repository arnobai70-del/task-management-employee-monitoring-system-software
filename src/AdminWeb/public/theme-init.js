// Applies the saved Admin Web theme before the first paint (see src/theme.tsx).
(function () {
  var preference = 'system';
  try { preference = window.localStorage.getItem('taskmonitoring-admin-theme') || 'system'; } catch (e) { /* storage unavailable */ }
  var dark = preference === 'dark' || (preference !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
})();
