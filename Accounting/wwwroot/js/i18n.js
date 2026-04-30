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
    return this;
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
    // data-i18n="key" → textContent
    scope.querySelectorAll('[data-i18n]').forEach(el => {
      const key = el.getAttribute('data-i18n');
      if (key) el.textContent = this.t(key);
    });
    // data-i18n-html="key" → innerHTML
    scope.querySelectorAll('[data-i18n-html]').forEach(el => {
      const key = el.getAttribute('data-i18n-html');
      if (key) el.innerHTML = this.t(key);
    });
    // data-i18n-placeholder="key"
    scope.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
      const key = el.getAttribute('data-i18n-placeholder');
      if (key) el.placeholder = this.t(key);
    });
    // data-i18n-title="key"
    scope.querySelectorAll('[data-i18n-title]').forEach(el => {
      const key = el.getAttribute('data-i18n-title');
      if (key) el.title = this.t(key);
    });
    // data-i18n-aria="key"
    scope.querySelectorAll('[data-i18n-aria]').forEach(el => {
      const key = el.getAttribute('data-i18n-aria');
      if (key) el.setAttribute('aria-label', this.t(key));
    });
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
