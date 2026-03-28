import { useState, useEffect, useCallback, useRef } from 'react';
import './App.css';
import { useTranslation } from './i18n/TranslationContext.jsx';

const PREFERENCE_VALUES = [
    { value: 'cafe' },
    { value: 'park', noOpenNow: true },
    { value: 'restaurant' },
    { value: 'bakery' },
    { value: 'supermarket' },
    { value: 'pharmacy' },
    { value: 'bar' },
    { value: 'museum' },
    { value: 'gym' },
    { value: 'library' },
    { value: 'bank' },
    { value: 'convenience_store' },
];

const TYPES_WITHOUT_OPEN_NOW = new Set(
    PREFERENCE_VALUES.filter(t => t.noOpenNow).map(t => t.value)
);

function LangSwitch() {
    const { lang, setLang } = useTranslation();
    return (
        <div className="wmw-lang-switch">
            <button
                className={`wmw-lang-btn${lang === 'de' ? ' wmw-lang-btn-active' : ''}`}
                onClick={() => setLang('de')}
            >DE</button>
            <button
                className={`wmw-lang-btn${lang === 'en' ? ' wmw-lang-btn-active' : ''}`}
                onClick={() => setLang('en')}
            >EN</button>
        </div>
    );
}

export default function App() {
    const { t } = useTranslation();

    const [regionState, setRegionState]       = useState('loading'); // 'loading' | 'allowed' | 'blocked'
    const [locationState, setLocationState]   = useState('idle');
    const [locationError, setLocationError]   = useState('');
    const [currentLocation, setCurrentLocation] = useState(null);
    const [currentAddress, setCurrentAddress] = useState('');
    const [destination, setDestination]       = useState('');
    const [suggestions, setSuggestions]       = useState([]);
    const [showSuggestions, setShowSuggestions] = useState(false);
    const [autocompleteLoading, setAutocompleteLoading] = useState(false);
    const [preferences, setPreferences]       = useState([]);
    const [preserveOrder, setPreserveOrder]   = useState(false);
    const [isLoading, setIsLoading]           = useState(false);
    const [routeResult, setRouteResult]       = useState(null);
    const [error, setError]                   = useState(null);
    const [flashMaxStops, setFlashMaxStops]   = useState(false);

    const debounceRef        = useRef(null);
    const abortControllerRef = useRef(null);
    const autocompleteRef    = useRef(null);
    const dragIndexRef       = useRef(null);
    const flashTimerRef      = useRef(null);

    // ── Region check ────────────────────────────────────────────────────────────
    useEffect(() => {
        fetch('/api/route/check-region')
            .then(r => r.json())
            .then(data => setRegionState(data.allowed ? 'allowed' : 'blocked'))
            .catch(() => setRegionState('allowed')); // network error → don't block
    }, []);

    // ── Geolocation ─────────────────────────────────────────────────────────────
    const resolvePosition = useCallback((position) => {
        const { latitude, longitude } = position.coords;
        setCurrentLocation({ lat: latitude, lng: longitude });
        fetch(`/api/route/address?lat=${latitude}&lng=${longitude}`)
            .then(r => r.json())
            .then(data => setCurrentAddress(data.address))
            .catch(() => setCurrentAddress(`${latitude.toFixed(6)}, ${longitude.toFixed(6)}`))
            .finally(() => setLocationState('ready'));
    }, []);

    const requestLocation = useCallback(() => {
        setLocationState('loading');
        setLocationError('');
        setRouteResult(null);
        setError(null);

        if (!navigator.geolocation) {
            setLocationError(t('location.error_not_supported'));
            setLocationState('denied');
            return;
        }

        navigator.geolocation.getCurrentPosition(
            resolvePosition,
            (err) => {
                if (err.code === 3) {
                    navigator.geolocation.getCurrentPosition(
                        resolvePosition,
                        () => {
                            setLocationError(t('location.error_timeout_final'));
                            setLocationState('denied');
                        },
                        { enableHighAccuracy: false, timeout: 15000, maximumAge: 60000 }
                    );
                } else {
                    const msgs = {
                        1: t('location.error_denied'),
                        2: t('location.error_unavailable'),
                        3: t('location.error_timeout'),
                    };
                    setLocationError(msgs[err.code] || t('location.error_unknown'));
                    setLocationState('denied');
                }
            },
            { enableHighAccuracy: true, timeout: 8000, maximumAge: 30000 }
        );
    }, [resolvePosition, t]);

    useEffect(() => {
        if (regionState === 'allowed') requestLocation();
    }, [regionState, requestLocation]);

    useEffect(() => {
        const handleClickOutside = (e) => {
            if (autocompleteRef.current && !autocompleteRef.current.contains(e.target))
                setShowSuggestions(false);
        };
        document.addEventListener('mousedown', handleClickOutside);
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, []);

    // ── Autocomplete ─────────────────────────────────────────────────────────────
    const handleDestinationChange = (value) => {
        setDestination(value);
        clearTimeout(debounceRef.current);

        if (value.trim().length < 2) {
            abortControllerRef.current?.abort();
            setSuggestions([]);
            setShowSuggestions(false);
            setAutocompleteLoading(false);
            return;
        }

        setShowSuggestions(true);
        setAutocompleteLoading(true);

        debounceRef.current = setTimeout(async () => {
            abortControllerRef.current?.abort();
            const controller = new AbortController();
            abortControllerRef.current = controller;

            try {
                const params = new URLSearchParams({ input: value });
                if (currentLocation) {
                    params.set('lat', currentLocation.lat);
                    params.set('lng', currentLocation.lng);
                }
                const res = await fetch(`/api/route/autocomplete?${params}`, { signal: controller.signal });
                if (res.ok) {
                    const data = await res.json();
                    setSuggestions(data.suggestions || []);
                }
            } catch (err) {
                if (err.name !== 'AbortError') setSuggestions([]);
            } finally {
                if (!controller.signal.aborted) setAutocompleteLoading(false);
            }
        }, 300);
    };

    const selectSuggestion = (suggestion) => {
        const combined = suggestion.address
            ? `${suggestion.description}, ${suggestion.address}`
            : suggestion.description;
        setDestination(combined);
        setSuggestions([]);
        setShowSuggestions(false);
    };

    // ── Preferences ──────────────────────────────────────────────────────────────
    const MAX_STOPS  = 4;
    const totalStops = preferences.reduce((sum, p) => sum + p.count, 0);

    const addPreference = () => {
        if (totalStops >= MAX_STOPS) {
            // Flash the counter
            clearTimeout(flashTimerRef.current);
            setFlashMaxStops(true);
            flashTimerRef.current = setTimeout(() => setFlashMaxStops(false), 900);
            return;
        }
        setPreferences(prev => [...prev, { id: Date.now(), type: 'cafe', count: 1, openNow: false }]);
    };

    const updateCount = (id, rawValue) => {
        const current = preferences.find(p => p.id === id);
        if (!current) return;
        const totalWithoutThis = totalStops - current.count;
        const clamped = Math.min(Math.max(1, parseInt(rawValue) || 1), MAX_STOPS - totalWithoutThis);
        updatePreference(id, 'count', clamped);
    };

    const updatePreference = (id, field, value) =>
        setPreferences(prev => prev.map(p => p.id === id ? { ...p, [field]: value } : p));

    const removePreference = (id) =>
        setPreferences(prev => prev.filter(p => p.id !== id));

    const handleDragStart = (index) => { dragIndexRef.current = index; };
    const handleDragOver  = (e, index) => {
        e.preventDefault();
        const from = dragIndexRef.current;
        if (from === null || from === index) return;
        setPreferences(prev => {
            const next = [...prev];
            const [moved] = next.splice(from, 1);
            next.splice(index, 0, moved);
            dragIndexRef.current = index;
            return next;
        });
    };
    const handleDragEnd = () => { dragIndexRef.current = null; };

    // ── Route ────────────────────────────────────────────────────────────────────
    const handleFindRoute = async () => {
        if (!destination.trim()) { setError(t('route.error_no_destination')); return; }
        setError(null);
        setIsLoading(true);
        setRouteResult(null);

        try {
            const res = await fetch('/api/route', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    currentLatitude:    currentLocation.lat,
                    currentLongitude:   currentLocation.lng,
                    destinationAddress: destination.trim(),
                    preserveOrder,
                    preferences: preferences.map(p => ({
                        type:    p.type,
                        count:   p.count,
                        openNow: !TYPES_WITHOUT_OPEN_NOW.has(p.type) && p.openNow
                    }))
                })
            });
            const data = await res.json();
            if (!res.ok) setError(data.error || t('route.error_failed'));
            else          setRouteResult(data);
        } catch {
            setError(t('route.error_server'));
        } finally {
            setIsLoading(false);
        }
    };

    // ── Region blocked ───────────────────────────────────────────────────────────
    if (regionState === 'loading') {
        return (
            <div className="wmw-app">
                <div className="wmw-card wmw-card-center">
                    <div className="wmw-spinner" />
                </div>
            </div>
        );
    }

    if (regionState === 'blocked') {
        return (
            <div className="wmw-app">
                <header className="wmw-header">
                    <div className="wmw-header-inner">
                        <svg className="wmw-logo" width="36" height="36" viewBox="0 0 36 36" fill="none">
                            <circle cx="18" cy="18" r="18" fill="#1e40af" />
                            <path d="M18 7C14.13 7 11 10.13 11 14C11 19.25 18 29 18 29C18 29 25 19.25 25 14C25 10.13 21.87 7 18 7Z" fill="white" />
                            <circle cx="18" cy="14" r="3.5" fill="#1e40af" />
                        </svg>
                        <div>
                            <h1 className="wmw-header-title">{t('header.title')}</h1>
                            <p className="wmw-header-sub">{t('header.subtitle')}</p>
                        </div>
                        <LangSwitch />
                    </div>
                </header>
                <main className="wmw-main">
                    <div className="wmw-card wmw-card-denied">
                        <div className="wmw-denied-icon">
                            <svg width="48" height="48" viewBox="0 0 48 48" fill="none">
                                <circle cx="24" cy="24" r="22" stroke="#ef4444" strokeWidth="2.5" />
                                <path d="M24 14v14M24 32v2" stroke="#ef4444" strokeWidth="3" strokeLinecap="round" />
                            </svg>
                        </div>
                        <h2 className="wmw-denied-title">{t('region.unavailable_title')}</h2>
                        <p className="wmw-denied-body">{t('region.unavailable_body')}</p>
                    </div>
                </main>
                <footer className="wmw-footer"><div className="wmw-footer-inner">{t('footer', { year: new Date().getFullYear() })}</div></footer>
            </div>
        );
    }

    // ── Main app ─────────────────────────────────────────────────────────────────
    return (
        <div className="wmw-app">
            <header className="wmw-header">
                <div className="wmw-header-inner">
                    <svg className="wmw-logo" width="36" height="36" viewBox="0 0 36 36" fill="none">
                        <circle cx="18" cy="18" r="18" fill="#1e40af" />
                        <path d="M18 7C14.13 7 11 10.13 11 14C11 19.25 18 29 18 29C18 29 25 19.25 25 14C25 10.13 21.87 7 18 7Z" fill="white" />
                        <circle cx="18" cy="14" r="3.5" fill="#1e40af" />
                    </svg>
                    <div>
                        <h1 className="wmw-header-title">{t('header.title')}</h1>
                        <p className="wmw-header-sub">{t('header.subtitle')}</p>
                    </div>
                    <LangSwitch />
                </div>
            </header>

            <main className="wmw-main">
                {locationState === 'loading' && (
                    <div className="wmw-card wmw-card-center">
                        <div className="wmw-spinner" />
                        <p className="wmw-loading-text">{t('location.loading')}</p>
                    </div>
                )}

                {locationState === 'denied' && (
                    <div className="wmw-card wmw-card-denied">
                        <div className="wmw-denied-icon">
                            <svg width="48" height="48" viewBox="0 0 48 48" fill="none">
                                <circle cx="24" cy="24" r="22" stroke="#ef4444" strokeWidth="2.5" />
                                <path d="M24 14v14M24 32v2" stroke="#ef4444" strokeWidth="3" strokeLinecap="round" />
                            </svg>
                        </div>
                        <h2 className="wmw-denied-title">{t('location.unavailable_title')}</h2>
                        <p className="wmw-denied-body">
                            {locationError || t('location.unavailable_body')}
                        </p>
                        <button className="wmw-btn wmw-btn-primary" onClick={requestLocation}>
                            {t('location.try_again')}
                        </button>
                    </div>
                )}

                {locationState === 'ready' && (
                    <>
                        <div className="wmw-card">
                            <div className="wmw-card-head">
                                <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
                                    <path d="M8 1C5.79 1 4 2.79 4 5C4 8.25 8 15 8 15C8 15 12 8.25 12 5C12 2.79 10.21 1 8 1ZM8 6.5C7.17 6.5 6.5 5.83 6.5 5C6.5 4.17 7.17 3.5 8 3.5C8.83 3.5 9.5 4.17 9.5 5C9.5 5.83 8.83 6.5 8 6.5Z" fill="#2563eb" />
                                </svg>
                                <h2 className="wmw-card-title">{t('location.card_title')}</h2>
                            </div>
                            <div className="wmw-field">
                                <label className="wmw-label">{t('location.current_position')}</label>
                                <input type="text" value={currentAddress} readOnly className="wmw-input wmw-input-readonly" />
                            </div>
                        </div>

                        <div className="wmw-card">
                            <div className="wmw-card-head">
                                <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
                                    <path d="M2 8h12M9 3l5 5-5 5" stroke="#2563eb" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" />
                                </svg>
                                <h2 className="wmw-card-title">{t('route.card_title')}</h2>
                            </div>

                            <div className="wmw-field">
                                <label className="wmw-label">{t('route.destination_label')}</label>
                                <div className="wmw-autocomplete" ref={autocompleteRef}>
                                    <input
                                        type="text"
                                        value={destination}
                                        onChange={e => handleDestinationChange(e.target.value)}
                                        onKeyDown={e => e.key === 'Enter' && handleFindRoute()}
                                        onFocus={() => suggestions.length > 0 && setShowSuggestions(true)}
                                        placeholder={t('route.destination_placeholder')}
                                        className={`wmw-input${showSuggestions ? ' wmw-input-has-suggestions' : ''}`}
                                    />
                                    {showSuggestions && (
                                        <div className="wmw-suggestions">
                                            {autocompleteLoading ? (
                                                <div className="wmw-suggestion wmw-suggestion-status">
                                                    <span className="wmw-spinner wmw-spinner-sm" />
                                                    <span>{t('autocomplete.searching')}</span>
                                                </div>
                                            ) : suggestions.length === 0 ? (
                                                <div className="wmw-suggestion wmw-suggestion-status wmw-suggestion-empty">
                                                    {t('autocomplete.no_results')}
                                                </div>
                                            ) : suggestions.map(s => (
                                                <div
                                                    key={s.placeId}
                                                    className="wmw-suggestion"
                                                    onMouseDown={e => { e.preventDefault(); selectSuggestion(s); }}
                                                >
                                                    <span className="wmw-suggestion-name">{s.description}</span>
                                                    {s.address && <span className="wmw-suggestion-addr">{s.address}</span>}
                                                </div>
                                            ))}
                                        </div>
                                    )}
                                </div>
                            </div>

                            <div className="wmw-prefs">
                                <div className="wmw-prefs-header">
                                    <div>
                                        <h3 className="wmw-prefs-title">{t('stops.title')}</h3>
                                        <p className={`wmw-prefs-hint${flashMaxStops ? ' wmw-prefs-hint-flash' : ''}`}>
                                            {t('stops.used', { n: totalStops, max: MAX_STOPS })}
                                        </p>
                                    </div>
                                    <button
                                        className="wmw-btn wmw-btn-add"
                                        onClick={addPreference}
                                        disabled={false}
                                    >
                                        {t('stops.add')}
                                    </button>
                                </div>

                                {preferences.length > 1 && (
                                    <label className="wmw-order-toggle">
                                        <input
                                            type="checkbox"
                                            checked={preserveOrder}
                                            onChange={e => setPreserveOrder(e.target.checked)}
                                        />
                                        <span>{t('stops.preserve_order')}</span>
                                    </label>
                                )}

                                {preferences.length === 0 && (
                                    <div className="wmw-prefs-empty">
                                        {t('stops.empty_before')} <strong>{t('stops.add')}</strong> {t('stops.empty_after')}
                                    </div>
                                )}

                                {preferences.length > 0 && (
                                    <div className="wmw-prefs-list">
                                        {preferences.map((pref, index) => (
                                            <div
                                                key={pref.id}
                                                className={`wmw-pref-row${preserveOrder ? ' wmw-pref-row-draggable' : ''}`}
                                                draggable={preserveOrder}
                                                onDragStart={() => handleDragStart(index)}
                                                onDragOver={e => handleDragOver(e, index)}
                                                onDragEnd={handleDragEnd}
                                            >
                                                {preserveOrder && <span className="wmw-drag-handle" aria-hidden="true">⠿</span>}
                                                <select
                                                    value={pref.type}
                                                    onChange={e => updatePreference(pref.id, 'type', e.target.value)}
                                                    className="wmw-select"
                                                >
                                                    {PREFERENCE_VALUES.map(pt => (
                                                        <option key={pt.value} value={pt.value}>{t(`pref.${pt.value}`)}</option>
                                                    ))}
                                                </select>
                                                <div className="wmw-count-group">
                                                    <input
                                                        type="number"
                                                        min="1"
                                                        max={MAX_STOPS - (totalStops - pref.count)}
                                                        value={pref.count}
                                                        onChange={e => updateCount(pref.id, e.target.value)}
                                                        className="wmw-input wmw-input-count"
                                                    />
                                                    {!TYPES_WITHOUT_OPEN_NOW.has(pref.type) && (
                                                        <label className="wmw-max-toggle">
                                                            <input
                                                                type="checkbox"
                                                                checked={pref.openNow}
                                                                onChange={e => updatePreference(pref.id, 'openNow', e.target.checked)}
                                                            />
                                                            <span>{t('stops.open_now')}</span>
                                                        </label>
                                                    )}
                                                </div>
                                                <button
                                                    className="wmw-btn-remove"
                                                    onClick={() => removePreference(pref.id)}
                                                    aria-label="Remove stop"
                                                >
                                                    &times;
                                                </button>
                                            </div>
                                        ))}
                                    </div>
                                )}
                            </div>

                            {error && <div className="wmw-error-banner">{error}</div>}

                            <button
                                className="wmw-btn wmw-btn-primary wmw-btn-full"
                                onClick={handleFindRoute}
                                disabled={isLoading}
                            >
                                {isLoading ? (
                                    <span className="wmw-btn-loading">
                                        <span className="wmw-spinner wmw-spinner-sm" />
                                        {t('route.calculating')}
                                    </span>
                                ) : t('route.find_button')}
                            </button>
                        </div>

                        {routeResult && (
                            <div className="wmw-card wmw-card-result">
                                <div className="wmw-card-head">
                                    <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
                                        <circle cx="8" cy="8" r="7" stroke="#22c55e" strokeWidth="1.75" />
                                        <path d="M4.5 8l2.5 2.5 5-5" stroke="#22c55e" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" />
                                    </svg>
                                    <h2 className="wmw-card-title">{t('result.card_title')}</h2>
                                </div>

                                <div className="wmw-route-meta">
                                    <div className="wmw-route-meta-row">
                                        <span className="wmw-meta-badge wmw-meta-from">{t('result.from')}</span>
                                        <span className="wmw-meta-addr">{currentAddress}</span>
                                    </div>
                                    <div className="wmw-route-meta-divider" />
                                    <div className="wmw-route-meta-row">
                                        <span className="wmw-meta-badge wmw-meta-to">{t('result.to')}</span>
                                        <span className="wmw-meta-addr">{routeResult.destinationAddress}</span>
                                    </div>
                                </div>

                                {routeResult.waypoints.length > 0 && (
                                    <div className="wmw-waypoints">
                                        <h3 className="wmw-waypoints-title">
                                            {routeResult.waypoints.length === 1
                                                ? t('result.stops_one',   { n: 1 })
                                                : t('result.stops_other', { n: routeResult.waypoints.length })}
                                        </h3>
                                        <ol className="wmw-waypoints-list">
                                            {routeResult.waypoints.map((wp, idx) => (
                                                <li key={idx} className="wmw-waypoint">
                                                    <span className="wmw-wp-num">{idx + 1}</span>
                                                    <div className="wmw-wp-info">
                                                        <div className="wmw-wp-name-row">
                                                            <span className="wmw-wp-name">{wp.name}</span>
                                                            {wp.isOpen === true  && <span className="wmw-badge wmw-badge-open">{t('result.badge_open')}</span>}
                                                            {wp.isOpen === false && <span className="wmw-badge wmw-badge-closed">{t('result.badge_closed')}</span>}
                                                        </div>
                                                        <span className="wmw-wp-type">
                                                            {t(`pref.${wp.type}`) || wp.type}
                                                        </span>
                                                        {wp.address && <span className="wmw-wp-addr">{wp.address}</span>}
                                                    </div>
                                                </li>
                                            ))}
                                        </ol>
                                    </div>
                                )}

                                {routeResult.waypoints.length === 0 && (
                                    <p className="wmw-no-waypoints">{t('result.no_stops')}</p>
                                )}

                                <a
                                    href={routeResult.googleMapsUrl}
                                    target="_blank"
                                    rel="noopener noreferrer"
                                    className="wmw-btn wmw-btn-maps wmw-btn-full"
                                >
                                    {t('result.open_maps')}
                                    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" style={{ marginLeft: '8px', flexShrink: 0 }}>
                                        <path d="M5.5 2.5H2C1.45 2.5 1 2.95 1 3.5V12C1 12.55 1.45 13 2 13H10.5C11.05 13 11.5 12.55 11.5 12V8.5M8.5 1H13V5.5M13 1L6 8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
                                    </svg>
                                </a>
                            </div>
                        )}
                    </>
                )}
            </main>

            <footer className="wmw-footer"><div className="wmw-footer-inner">{t('footer', { year: new Date().getFullYear() })}</div></footer>
        </div>
    );
}
