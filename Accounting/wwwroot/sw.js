const CACHE_VERSION = 'nextacc-v3';
const CACHE_NAME = CACHE_VERSION;

self.addEventListener('install', event => {
  self.skipWaiting();
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

self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);

  if (event.request.method !== 'GET') return;
  if (url.origin !== self.location.origin) return;

  // API calls: network-first with offline fallback
  if (url.pathname.startsWith('/api/')) {
    event.respondWith(
      fetch(event.request)
        .then(response => {
          if (response.ok) {
            const clone = response.clone();
            caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
          }
          return response;
        })
        .catch(() => caches.match(event.request))
    );
    return;
  }

  // HTML/JS/CSS: ALWAYS network-first so code updates show immediately
  // Cache is used only as offline fallback
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
        .catch(() => caches.match(event.request).then(cached => cached || caches.match('/app.html')))
    );
    return;
  }

  // Images/fonts: cache-first (they rarely change)
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
