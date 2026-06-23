/*
 * Service worker for the Attendance Monitoring PWA.
 *
 *   - Pre-caches a small static shell so the login / dashboard pages
 *     keep loading even when the device drops connectivity (only the
 *     shell loads; live data still requires a working network).
 *   - Network-first strategy for HTML so users always see the latest
 *     state when online, falling back to the cached shell otherwise.
 *   - Stale-while-revalidate for /css and /js so static assets stay
 *     snappy without going stale.
 *   - Push handler renders a notification when the backend (via VAPID)
 *     pushes a payload to one of the registered subscriptions.
 */
const CACHE_NAME = 'attendance-shell-v1';
const SHELL = [
    '/',
    '/css/style.css',
    '/js/table-sort.js',
    '/manifest.json',
    '/favicon.svg',
];

self.addEventListener('install', function (event) {
    event.waitUntil(
        caches.open(CACHE_NAME).then(function (cache) {
            return cache.addAll(SHELL).catch(function () { /* best-effort */ });
        })
    );
    self.skipWaiting();
});

self.addEventListener('activate', function (event) {
    event.waitUntil(
        caches.keys().then(function (keys) {
            return Promise.all(
                keys.filter(function (k) { return k !== CACHE_NAME; })
                    .map(function (k) { return caches.delete(k); })
            );
        })
    );
    self.clients.claim();
});

self.addEventListener('fetch', function (event) {
    const req = event.request;
    if (req.method !== 'GET') return;
    const url = new URL(req.url);
    // Only cache same-origin requests; never cache POSTs or API calls.
    if (url.origin !== self.location.origin) return;
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/swagger')) return;

    if (req.destination === 'document') {
        // HTML: network-first, fall back to cache.
        event.respondWith(
            fetch(req)
                .then(function (resp) {
                    const copy = resp.clone();
                    caches.open(CACHE_NAME).then(function (c) { c.put(req, copy); });
                    return resp;
                })
                .catch(function () { return caches.match(req).then(function (r) { return r || caches.match('/'); }); })
        );
        return;
    }

    if (req.destination === 'style' || req.destination === 'script' || req.destination === 'image') {
        // Stale-while-revalidate.
        event.respondWith(
            caches.match(req).then(function (cached) {
                const network = fetch(req).then(function (resp) {
                    const copy = resp.clone();
                    caches.open(CACHE_NAME).then(function (c) { c.put(req, copy); });
                    return resp;
                }).catch(function () { return cached; });
                return cached || network;
            })
        );
    }
});

self.addEventListener('push', function (event) {
    let payload = { title: 'Attendance', body: 'You have a new notification.' };
    try { if (event.data) payload = Object.assign(payload, event.data.json()); }
    catch (e) { /* keep defaults */ }

    event.waitUntil(
        self.registration.showNotification(payload.title || 'Attendance', {
            body: payload.body || '',
            icon: '/favicon.svg',
            badge: '/favicon.svg',
            tag: payload.tag || 'attendance',
            data: payload,
        })
    );
});

self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    const target = (event.notification.data && event.notification.data.url) || '/';
    event.waitUntil(
        self.clients.matchAll({ type: 'window' }).then(function (windows) {
            for (const w of windows) {
                if (w.url.endsWith(target) && 'focus' in w) return w.focus();
            }
            return self.clients.openWindow(target);
        })
    );
});
