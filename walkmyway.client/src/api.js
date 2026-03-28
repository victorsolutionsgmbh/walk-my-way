// Transparent authenticated fetch wrapper.
// Obtains a JWT from /api/auth on first use and re-obtains it when expired.
// All API calls that require authorization should use authFetch() instead of fetch().

const API_KEY = import.meta.env.VITE_API_KEY;

let _token = null;
let _tokenExp = 0;         // Unix ms
let _refreshPromise = null; // coalesce concurrent refresh calls

function isExpired() {
    // Treat token as expired 30 seconds before actual expiry to avoid edge-case races
    return !_token || Date.now() >= _tokenExp - 30_000;
}

async function refreshToken() {
    if (_refreshPromise) return _refreshPromise;
    _refreshPromise = (async () => {
        const res = await fetch('/api/auth', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ apiKey: API_KEY })
        });
        if (!res.ok) throw new Error(`Authentication failed (${res.status})`);
        const { token } = await res.json();
        _token = token;
        // Decode exp from JWT payload (no external library needed)
        const payload = JSON.parse(atob(token.split('.')[1]));
        _tokenExp = payload.exp * 1000;
    })().finally(() => { _refreshPromise = null; });
    return _refreshPromise;
}

export async function authFetch(url, options = {}) {
    if (isExpired()) await refreshToken();

    const res = await fetch(url, {
        ...options,
        headers: { ...options.headers, Authorization: `Bearer ${_token}` }
    });

    // If the server rejects the token (e.g. server restarted with new secret),
    // refresh once and retry.
    if (res.status === 401) {
        _token = null;
        await refreshToken();
        return fetch(url, {
            ...options,
            headers: { ...options.headers, Authorization: `Bearer ${_token}` }
        });
    }

    return res;
}
