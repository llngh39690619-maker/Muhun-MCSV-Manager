"use strict";

const CACHE_NAME = "mcsv-offline-product-v2";
const FALLBACK_CULTURE = "zh-TW";
const OFFLINE_URLS = Object.freeze({
    "zh-TW": "/offline.html?culture=zh-TW",
    "en-US": "/offline.html?culture=en-US"
});
const MANIFEST_URLS = Object.freeze({
    "zh-TW": "/manifest.webmanifest?culture=zh-TW",
    "en-US": "/manifest.webmanifest?culture=en-US"
});
const OFFLINE_ASSETS = Object.freeze([
    ...Object.values(OFFLINE_URLS),
    ...Object.values(MANIFEST_URLS),
    "/offline.css",
    "/icon-180.png",
    "/icon-192.png",
    "/icon-512.png",
    "/icon-maskable-512.png"
]);
const OFFLINE_SUPPORT_PATHS = new Set([
    "/offline.css",
    "/icon-180.png",
    "/icon-192.png",
    "/icon-512.png",
    "/icon-maskable-512.png"
]);

function normalizeCulture(value) {
    const language = String(value || "").trim().toLowerCase().split("-")[0];
    return language === "en" ? "en-US" : FALLBACK_CULTURE;
}

function localizedAssetUrl(url, assets) {
    return assets[normalizeCulture(url.searchParams.get("culture"))];
}

self.addEventListener("install", event => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => cache.addAll(OFFLINE_ASSETS))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener("activate", event => {
    event.waitUntil(
        caches.keys()
            .then(names => Promise.all(names
                .filter(name => name.startsWith("mcsv-offline-") && name !== CACHE_NAME)
                .map(name => caches.delete(name))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener("fetch", event => {
    const request = event.request;
    if (request.method !== "GET") {
        return;
    }

    const url = new URL(request.url);
    if (url.origin !== self.location.origin || url.pathname.startsWith("/api/")) {
        return;
    }

    if (request.mode === "navigate" && (url.pathname === "/" || url.pathname === "/index.html")) {
        event.respondWith(
            fetch(request)
                .catch(() => caches.match(localizedAssetUrl(url, OFFLINE_URLS)))
        );
        return;
    }

    if (url.pathname === "/offline.html") {
        event.respondWith(
            caches.match(localizedAssetUrl(url, OFFLINE_URLS))
                .then(cached => cached || fetch(request))
        );
        return;
    }

    if (url.pathname === "/manifest.webmanifest") {
        event.respondWith(
            caches.match(localizedAssetUrl(url, MANIFEST_URLS))
                .then(cached => cached || fetch(request))
        );
        return;
    }

    if (OFFLINE_SUPPORT_PATHS.has(url.pathname)) {
        event.respondWith(
            caches.match(request)
                .then(cached => cached || fetch(request))
        );
    }
});
