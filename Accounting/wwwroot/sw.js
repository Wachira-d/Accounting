// v5: ล้าง cache เก่าทั้งหมด (เคยมี API response ค้างใน Cache Storage ของ SW
// รุ่นเก่า ทำหน้าเงินมัดจำโชว์ 0 ทั้งที่ backend มีข้อมูล) — bump version →
// activate จะ caches.delete ทุก key เก่า + api.js ใส่ _t cache-bust แล้ว
const CACHE_VERSION = 'nextacc-v5';
const CACHE_NAME = CACHE_VERSION;

// POS shell + critical assets pre-cached on install so the cashier can open
// the page even when the device boots with no internet. Adding/removing
// entries here invalidates the cache (bump CACHE_VERSION when shipping).
const POS_SHELL = [
  '/pages/pos.html',
  '/pages/pos-kds.html',
  '/css/style.css',
  '/js/layout.js',
  '/js/api.js',
  '/js/translations.js',
  '/js/i18n.js',
  '/pos-manifest.json',
  '/manifest.json',
  '/assets/icon-192.png',
  '/assets/icon-512.png',
];

// API GETs we want available offline (product list, packages, modifiers,
// current session, etc.). Network-first with cache fallback — the first
// online load fills the cache, after that the page can boot offline with
// the most recent snapshot.
const POS_API_PREFIXES = [
  '/products',          // /api/companies/{id}/products
  '/pos/service-packages',
  '/pos/modifier-groups',
  '/pos/sessions',
  '/pos/terminals',
];

self.addEventListener('install', event => {
  // Pre-cache the POS shell. Use addAll for atomic install — if any one
  // entry fails the whole cache is rejected (forces a clean retry on next
  // load). individual fetches use { cache: 'reload' } so we don't pick
  // up a stale browser-cached copy when bumping CACHE_VERSION.
  event.waitUntil(
    caches.open(CACHE_NAME).then(cache =>
      Promise.all(POS_SHELL.map(url =>
        fetch(url, { cache: 'reload' })
          .then(r => r.ok ? cache.put(url, r) : Promise.resolve())
          .catch(() => Promise.resolve()) // shouldn't block install if one optional file is missing
      ))
    ).then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(keys =>
      Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))
    ).then(() => self.clients.claim())
  );
});

// Message handler: allow page to force skipWaiting (for update prompt)
self.addEventListener('message', event => {
  if (event.data === 'SKIP_WAITING') self.skipWaiting();
});

function isCacheableApiRead(url) {
  if (!url.pathname.startsWith('/api/')) return false;
  return POS_API_PREFIXES.some(p => url.pathname.includes(p));
}

self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);

  if (event.request.method !== 'GET') return;
  if (url.origin !== self.location.origin) return;

  // API calls: network-first; only API endpoints we explicitly want offline
  // get cached (POS read endpoints). Posts/puts/deletes bypass entirely so
  // they fail with a network error which the page can catch (and queue the
  // sale in IndexedDB for later sync).
  if (url.pathname.startsWith('/api/')) {
    const cacheable = isCacheableApiRead(url);
    event.respondWith(
      fetch(event.request)
        .then(response => {
          if (response.ok && cacheable) {
            const clone = response.clone();
            caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
          }
          return response;
        })
        .catch(() => cacheable ? caches.match(event.request) : Promise.reject(new Error('offline')))
    );
    return;
  }

  // HTML/JS/CSS: network-first so code updates show immediately when online.
  // When offline, fall back to the cached copy, then to the POS shell as a
  // last resort so the cashier never sees the browser's "no internet" page.
  const isCode = /\.(html|js|css|json)$/i.test(url.pathname) || url.pathname === '/' || !/\.[a-z0-9]+$/i.test(url.pathname);
  if (isCode) {
    event.respondWith(
      fetch(event.request, { cache: 'no-store' })
        .then(response => {
          if (response.ok) {
            const clone = response.clone();
            caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
          }
          return response;
        })
        .catch(() => caches.match(event.request).then(cached =>
          cached || caches.match('/pages/pos.html') || caches.match('/app.html')))
    );
    return;
  }

  // Images / fonts / other static assets: cache-first (rarely change).
  event.respondWith(
    caches.match(event.request).then(cached => {
      if (cached) return cached;
      return fetch(event.request).then(response => {
        if (response.ok) {
          const clone = response.clone();
          caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
        }
        return response;
      });
    })
  );
});

self.addEventListener('push', event => {
  const data = event.data?.json() || { title: 'Next Acc', body: 'คุณมีการแจ้งเตือนใหม่' };
  event.waitUntil(
    self.registration.showNotification(data.title, {
      body: data.body,
      icon: '/assets/icon-192.png',
      badge: '/assets/icon-192.png',
      data: data.url || '/app.html'
    })
  );
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  event.waitUntil(clients.openWindow(event.notification.data));
});
