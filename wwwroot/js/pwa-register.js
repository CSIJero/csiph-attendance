/*
 * PWA bootstrap. Registers the service worker, and — when the browser
 * supports Push and the server has VAPID configured — subscribes the
 * device so the backend can push notifications. All failures are
 * silenced; the dashboard works fine without push.
 */
(function () {
    if (!('serviceWorker' in navigator)) return;

    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/service-worker.js')
            .then(function (reg) {
                // Only attempt push setup when the user is signed in
                // (data-authed="1" lives on <body>) AND the browser
                // exposes PushManager.
                var body = document.body;
                if (!body || body.getAttribute('data-authed') !== '1') return;
                if (!('PushManager' in window)) return;
                setupPush(reg).catch(function () { /* silent */ });
            })
            .catch(function () { /* silent */ });
    });

    async function setupPush(reg) {
        // Notification permission — only ask once. We don't pester the
        // user with a permission prompt on every page; we wait for them
        // to opt in via the dashboard banner (TODO future work).
        if (Notification.permission === 'denied') return;
        if (Notification.permission === 'default') {
            // Don't pop the permission dialog automatically. Bail out
            // and let the user trigger it explicitly later.
            return;
        }

        var keyResp = await fetch('/api/push/vapid-public-key');
        if (!keyResp.ok) return;
        var keyJson = await keyResp.json();
        if (!keyJson || !keyJson.key) return;

        var existing = await reg.pushManager.getSubscription();
        if (existing) return; // already subscribed

        var sub = await reg.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: urlBase64ToUint8Array(keyJson.key),
        });

        await fetch('/api/push/subscribe', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(sub),
        });
    }

    function urlBase64ToUint8Array(base64String) {
        var padding = '='.repeat((4 - base64String.length % 4) % 4);
        var base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        var raw = atob(base64);
        var out = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
        return out;
    }
})();
