namespace WalkMyWay.Server;

public static class AppConstants
{
    // ── Route calculation ────────────────────────────────────────────────────────

    /// <summary>Maximum number of waypoint stops a user can request in a single route.</summary>
    public const int MaxStops = 40;

    /// <summary>
    /// Max distance (metres) between a candidate stop and the current route position
    /// at which a backwards stop is still allowed and flagged as a backtrack.
    /// </summary>
    public const int BacktrackThresholdM = 100;

    /// <summary>Number of sample points taken along the route when searching for nearby places.</summary>
    public const int RouteSamplePoints = 5;

    /// <summary>Extra candidates fetched per preference type in preserve-order mode to reduce gaps.</summary>
    public const int SearchCountBuffer = 4;

    /// <summary>Minimum distance (metres) between two selected waypoints of the same type.</summary>
    public const int MinWaypointDistanceM = 100;

    /// <summary>Radius (metres) around a selected stop to search for alternative candidates.</summary>
    public const int AlternativesRadiusM = 400;

    /// <summary>Maximum number of alternative stops attached to each selected waypoint.</summary>
    public const int MaxAlternatives = 3;

    // ── Autocomplete ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Radius (metres) used for all ST_Expand bounding-box pre-filters in autocomplete queries.
    /// Larger values return more candidates but increase query time.
    /// </summary>
    public const int AutocompleteSearchRadiusM = 100000;

    /// <summary>Radius (metres) used in LATERAL joins to resolve PLZ/city and nearest-street fallbacks.</summary>
    public const int AutocompleteFallbackRadiusM = 5000;

    /// <summary>Maximum number of suggestions returned by the autocomplete endpoint.</summary>
    public const int AutocompleteMaxResults = 8;

    /// <summary>Maximum entries in the in-process autocomplete suggestion cache.</summary>
    public const int AutocompleteCacheMaxSize = 200;

    /// <summary>Minimum input length required to trigger category-based POI search (e.g. "bar", "café").</summary>
    public const int AutocompleteCategoryMinLength = 3;

    /// <summary>Minimum pg_trgm similarity score for fuzzy street-name matching in address resolution.</summary>
    public const string FuzzyStreetSimilarityThreshold = "0.45";

    // ── Google Maps API / PostgresOsmProvider ────────────────────────────────────

    /// <summary>Maximum entries in the reverse-geocode result cache.</summary>
    public const int GeoCacheMaxSize = 500;

    /// <summary>Minimum interval (milliseconds) between outgoing Google Maps API requests (rate limiter).</summary>
    public const int GoogleApiMinRequestIntervalMs = 150;

    /// <summary>Maximum number of characters logged from a Google API response body before truncation.</summary>
    public const int ApiLogResponsePreviewLength = 2000;
}
