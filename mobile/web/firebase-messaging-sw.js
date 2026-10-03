// Push for the web build of the app (docs/DECISIONS.md T-146).
//
// Firebase Cloud Messaging registers this worker itself, in its own scope, and
// shows a push while no NeoPrint tab is in front. A click opens the app on the
// notification's screen: "/?link=neoprint://...&notificationId=..." - the app
// reads those on start (lib/features/notifications/notification_host.dart).
//
// The Firebase JS SDK is the version firebase_core_web loads in the page
// (12.19.0, mobile/pubspec.yaml); the configuration is the web app's from
// lib/firebase_options.dart - public identifiers, not secrets.

// Registered before Firebase's own handler, so it decides what a click does.
self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  event.stopImmediatePropagation();

  // The compat SDK keeps the FCM message under FCM_MSG.
  const stored = event.notification.data || {};
  const data = (stored.FCM_MSG && stored.FCM_MSG.data) || stored.data || {};

  const target = new URL('/', self.location.origin);
  if (data.link) {
    target.searchParams.set('link', data.link);
  }
  if (data.notificationId) {
    target.searchParams.set('notificationId', data.notificationId);
  }

  event.waitUntil(
    (async () => {
      const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
      const open = windows.find((client) => new URL(client.url).origin === self.location.origin);
      if (open) {
        // The tab is not this worker's to navigate; it is told instead.
        await open.focus();
        open.postMessage({ neoprintOpen: target.href });
        return;
      }
      await self.clients.openWindow(target.href);
    })(),
  );
});

importScripts('https://www.gstatic.com/firebasejs/12.19.0/firebase-app-compat.js');
importScripts('https://www.gstatic.com/firebasejs/12.19.0/firebase-messaging-compat.js');

firebase.initializeApp({
  apiKey: 'AIzaSyDTsoLxfi0HZrF-fo92EFODHbIC57_wSH8',
  authDomain: 'neoprint-8d6e1.firebaseapp.com',
  projectId: 'neoprint-8d6e1',
  storageBucket: 'neoprint-8d6e1.firebasestorage.app',
  messagingSenderId: '168056208876',
  appId: '1:168056208876:web:3321eaa99b44da4178d4c9',
});

firebase.messaging();
