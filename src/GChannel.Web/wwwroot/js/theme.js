// Bootstrap 5.3 colour-mode helper. Loaded in <head> so the saved theme is applied before first paint
// (no light flash); MainLayout drives it afterwards through the same object.
window.gchannelTheme = {
    storageKey: 'gchannel-theme',
    prefersDark: () => window.matchMedia('(prefers-color-scheme: dark)').matches,
    apply: (mode) => document.documentElement.setAttribute('data-bs-theme', mode),
    init: function () {
        const stored = localStorage.getItem(this.storageKey);
        this.apply(stored === 'dark' || stored === 'light' ? stored : (this.prefersDark() ? 'dark' : 'light'));
    }
};
window.gchannelTheme.init();
