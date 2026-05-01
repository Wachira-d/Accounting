// ===== Next Acc Internationalization (i18n) Engine =====
// Supports Thai (th) and English (en) with data-i18n attributes.
// Usage: I18n.init() on page load; I18n.apply() after dynamic content.

const I18n = {
  _lang: 'th',
  _listeners: [],

  get lang() { return this._lang; },

  init() {
    const saved = localStorage.getItem('nextacc_lang');
    if (saved && (saved === 'th' || saved === 'en')) {
      this._lang = saved;
    } else {
      const nav = (navigator.language || navigator.userLanguage || '').toLowerCase();
      this._lang = nav.startsWith('en') ? 'en' : 'th';
    }
    document.documentElement.lang = this._lang;
    this.apply();
    this._installAutoApply();
    return this;
  },

  // Auto-translate dynamically-added content via MutationObserver
  _installAutoApply() {
    if (this._observer || typeof MutationObserver === 'undefined') return;
    let pending = false;
    const dirty = new Set();
    this._observer = new MutationObserver(muts => {
      for (const m of muts) {
        for (const n of m.addedNodes) {
          if (n.nodeType === 1) dirty.add(n);
        }
      }
      if (pending || dirty.size === 0) return;
      pending = true;
      requestAnimationFrame(() => {
        pending = false;
        const nodes = Array.from(dirty);
        dirty.clear();
        for (const node of nodes) {
          try { this.apply(node); } catch {}
        }
      });
    });
    this._observer.observe(document.body, { childList: true, subtree: true });
  },

  setLang(lang) {
    if (lang !== 'th' && lang !== 'en') return;
    this._lang = lang;
    localStorage.setItem('nextacc_lang', lang);
    document.documentElement.lang = lang;
    document.title = this.t(document.title._i18nKey || '') || document.title;
    this.apply();
    this._listeners.forEach(fn => { try { fn(lang); } catch {} });
  },

  onLangChange(fn) { this._listeners.push(fn); },

  t(key, vars) {
    if (!key) return '';
    const dict = (typeof I18nData !== 'undefined') ? I18nData[this._lang] : null;
    if (!dict) return key;
    const val = key.split('.').reduce((o, k) => o && o[k], dict);
    if (val == null) return key;
    if (!vars) return val;
    return val.replace(/\{(\w+)\}/g, (_, k) => vars[k] != null ? vars[k] : `{${k}}`);
  },

  apply(root) {
    const scope = root || document;
    const isElement = scope.nodeType === 1;

    const applyOne = (el) => {
      const k1 = el.getAttribute && el.getAttribute('data-i18n');
      if (k1) el.textContent = this.t(k1);
      const k2 = el.getAttribute && el.getAttribute('data-i18n-html');
      if (k2) el.innerHTML = this.t(k2);
      const k3 = el.getAttribute && el.getAttribute('data-i18n-placeholder');
      if (k3) el.placeholder = this.t(k3);
      const k4 = el.getAttribute && el.getAttribute('data-i18n-title');
      if (k4) el.title = this.t(k4);
      const k5 = el.getAttribute && el.getAttribute('data-i18n-aria');
      if (k5) el.setAttribute('aria-label', this.t(k5));
    };

    if (isElement) applyOne(scope);
    scope.querySelectorAll('[data-i18n], [data-i18n-html], [data-i18n-placeholder], [data-i18n-title], [data-i18n-aria]').forEach(applyOne);
    // <title> tag
    const titleKey = document.querySelector('title')?.getAttribute('data-i18n');
    if (titleKey) document.title = this.t(titleKey);
    // Language toggle buttons
    scope.querySelectorAll('[data-lang-toggle]').forEach(btn => {
      const btnLang = btn.getAttribute('data-lang-toggle');
      btn.classList.toggle('active', btnLang === this._lang);
    });
  },

  // Render a language switcher component
  renderSwitcher(containerId) {
    const el = document.getElementById(containerId);
    if (!el) return;
    el.innerHTML = `<div class="lang-switcher">
      <button data-lang-toggle="th" onclick="I18n.setLang('th')" class="${this._lang === 'th' ? 'active' : ''}">TH</button>
      <button data-lang-toggle="en" onclick="I18n.setLang('en')" class="${this._lang === 'en' ? 'active' : ''}">EN</button>
    </div>`;
  }
};
