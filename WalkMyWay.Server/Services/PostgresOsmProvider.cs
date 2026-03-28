using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using WalkMyWay.Server.Models;

namespace WalkMyWay.Server.Services;

/// <summary>
/// IMapProvider backed by a local osm2pgsql PostgreSQL database.
///
/// - Reverse geocoding, place search, autocomplete  → PostGIS queries
/// - Walking route calculation                       → Google Maps Directions API
/// - Navigation URL                                  → Google Maps format
///
/// Requires:
///   ConnectionStrings:Osm   — PostgreSQL connection string
///   GoogleMaps:ApiKey       — for routing
/// </summary>
public class PostgresOsmProvider : IMapProvider
{
    private readonly NpgsqlDataSource _db;
    private readonly HttpClient _httpClient;
    private readonly string _googleApiKey;
    private readonly GoogleApiLogger _apiLogger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);

    // ── Reverse-geocode cache ───────────────────────────────────────────────────
    // Key: lat/lng rounded to 4 decimal places (~11 m precision).
    // Bounded to 500 entries; cleared wholesale when full (simple, allocation-free eviction).
    private static readonly ConcurrentDictionary<string, string> _geocodeCache = new();
    private const int GeoCacheMaxSize = 500;
    private static string GeoCacheKey(double lat, double lng) => $"{lat:F4},{lng:F4}";

    // ── Autocomplete cache ──────────────────────────────────────────────────────
    // Key: trimmed lower-case input + location rounded to ~1 km (F2).
    // Autocomplete results are stable between OSM imports, so no expiry needed.
    private static readonly ConcurrentDictionary<string, List<PlaceSuggestion>> _autocompleteCache = new();
    private const int AutocompleteCacheMaxSize = 200;
    private static string AutocompleteCacheKey(string q, double? lat, double? lng) =>
        $"{q.ToLowerInvariant()}|{(lat.HasValue ? $"{lat:F2}" : "_")}|{(lng.HasValue ? $"{lng:F2}" : "_")}";
    private static DateTime _lastRequestTime = DateTime.MinValue;
    private const int MinRequestIntervalMs = 150;

    public PostgresOsmProvider(
        NpgsqlDataSource db,
        HttpClient httpClient,
        IConfiguration configuration,
        GoogleApiLogger apiLogger)
    {
        _db = db;
        _httpClient = httpClient;
        _apiLogger = apiLogger;
        _googleApiKey = configuration["GoogleMaps:ApiKey"]
            ?? throw new InvalidOperationException("GoogleMaps:ApiKey is not configured.");
    }

    // ── Reverse geocoding ──────────────────────────────────────────────────────

    public async Task<string> ReverseGeocodeAsync(double lat, double lng)
    {
        var cacheKey = GeoCacheKey(lat, lng);
        if (_geocodeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Single round-trip: address point → address polygon → nearest street (by priority).
        // Uses native EPSG:3857 KNN (<->) so PostGIS can use the GIST index on `way` directly.
        // The ST_Expand bounding-box pre-filter (~150 m at Vienna lat) limits the index scan
        // to a tiny neighbourhood before the exact KNN sort.
        const string sql = """
            WITH pt AS (
                SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), 3857) AS geom
            )
            SELECT prio, street, nr, city
            FROM (
                -- 1. Address node (point)
                (
                    SELECT 1                       AS prio,
                           tags->'addr:street'     AS street,
                           "addr:housenumber"      AS nr,
                           tags->'addr:city'       AS city,
                           way <-> pt.geom         AS dist
                    FROM   planet_osm_point, pt
                    WHERE  tags->'addr:street' IS NOT NULL
                      AND  "addr:housenumber"  IS NOT NULL
                      AND  way && ST_Expand(pt.geom, 200)
                    ORDER BY dist
                    LIMIT  1
                )
                UNION ALL
                -- 2. Address building polygon
                (
                    SELECT 2,
                           tags->'addr:street',
                           "addr:housenumber",
                           tags->'addr:city',
                           way <-> pt.geom
                    FROM   planet_osm_polygon, pt
                    WHERE  tags->'addr:street' IS NOT NULL
                      AND  "addr:housenumber"  IS NOT NULL
                      AND  way && ST_Expand(pt.geom, 200)
                    ORDER BY way <-> pt.geom
                    LIMIT  1
                )
                UNION ALL
                -- 3. Nearest named street (fallback — no radius limit, pure KNN)
                (
                    SELECT 3,
                           name,
                           NULL,
                           NULL,
                           way <-> pt.geom
                    FROM   planet_osm_line, pt
                    WHERE  name    IS NOT NULL
                      AND  highway IS NOT NULL
                    ORDER BY way <-> pt.geom
                    LIMIT  1
                )
            ) candidates
            ORDER BY prio, dist
            LIMIT  1
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("lng", lng);
        cmd.Parameters.AddWithValue("lat", lat);

        await using var r = await cmd.ExecuteReaderAsync();
        string result;
        if (await r.ReadAsync())
        {
            var street = r.IsDBNull(1) ? null : r.GetString(1);
            var nr     = r.IsDBNull(2) ? null : r.GetString(2);
            var city   = r.IsDBNull(3) ? null : r.GetString(3);
            result = FormatAddress(null, street, nr, city);
            if (string.IsNullOrEmpty(result))
                result = $"{lat:F6}, {lng:F6}";
        }
        else
        {
            result = $"{lat:F6}, {lng:F6}";
        }

        if (_geocodeCache.Count >= GeoCacheMaxSize)
            _geocodeCache.Clear();
        _geocodeCache[cacheKey] = result;

        return result;
    }

    // ── Walking route — Google Maps Directions API ─────────────────────────────

    public async Task<RouteData> GetWalkingRouteAsync(double originLat, double originLng, string destination)
    {
        var sanitizedDestination = destination.Replace("'", "");
        var url = $"https://maps.googleapis.com/maps/api/directions/json" +
                  $"?origin={originLat.ToString(CultureInfo.InvariantCulture)},{originLng.ToString(CultureInfo.InvariantCulture)}" +
                  $"&destination={Uri.EscapeDataString(sanitizedDestination)}" +
                  $"&mode=walking&key={_googleApiKey}";

        var json     = await ThrottledGetStringAsync(url);
        var response = JsonSerializer.Deserialize<DirectionsApiResponse>(json, JsonOptions);

        if (response?.Status != "OK" || response.Routes == null || response.Routes.Length == 0)
            throw new InvalidOperationException("No walking route found to the specified destination.");

        var route      = response.Routes[0];
        var points     = DecodePolyline(route.OverviewPolyline.Points);
        var endAddress = route.Legs?.FirstOrDefault()?.EndAddress ?? destination;

        return new RouteData(points, endAddress);
    }

    // ── Nearby place search ────────────────────────────────────────────────────

    public async Task<List<PlaceCandidate>> SearchPlacesNearbyAsync(
        double lat, double lng, int radiusMeters, string type, bool openNow)
    {
        var (table, column, values) = GetOsmFilter(type);

        if (table == "planet_osm_polygon")
            return await SearchPolygonsNearbyAsync(lat, lng, radiusMeters, column, values, openNow);

        return await SearchPointsNearbyAsync(lat, lng, radiusMeters, column, values, openNow);
    }

    private async Task<List<PlaceCandidate>> SearchPointsNearbyAsync(
        double lat, double lng, int radiusMeters, string column, string[] values, bool openNow)
    {
        // Fetch more rows than needed so we still have enough after open-now filtering.
        // LATERAL join finds the nearest street name for POIs that have no addr:street tag.
        var sql = $"""
            SELECT p.osm_id::text,
                   p.name,
                   p.tags->'addr:street'      AS street,
                   p."addr:housenumber"       AS housenumber,
                   p.tags->'addr:city'        AS city,
                   ST_X(ST_Transform(p.way, 4326)) AS lng,
                   ST_Y(ST_Transform(p.way, 4326)) AS lat,
                   p.tags->'opening_hours'   AS opening_hours,
                   nearest.name              AS fallback_street
            FROM   planet_osm_point p
            LEFT JOIN LATERAL (
                SELECT name FROM planet_osm_line
                WHERE  highway IS NOT NULL AND name IS NOT NULL
                ORDER BY way <-> p.way
                LIMIT  1
            ) nearest ON (p.tags->'addr:street') IS NULL
            WHERE  p.{column} = ANY(@values)
              AND  p.name IS NOT NULL
              AND  ST_DWithin(
                     ST_Transform(p.way, 4326)::geography,
                     ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography,
                     @radius)
            ORDER BY p.way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), 3857)
            LIMIT  {(openNow ? 40 : 10)}
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("values", values);
        cmd.Parameters.AddWithValue("lng", lng);
        cmd.Parameters.AddWithValue("lat", lat);
        cmd.Parameters.AddWithValue("radius", radiusMeters);

        return await ReadPlaceCandidates(cmd, hasViewport: false, openNow);
    }

    private async Task<List<PlaceCandidate>> SearchPolygonsNearbyAsync(
        double lat, double lng, int radiusMeters, string column, string[] values, bool openNow)
    {
        var sql = $"""
            SELECT p.osm_id::text,
                   p.name,
                   p.tags->'addr:street'      AS street,
                   p."addr:housenumber"       AS housenumber,
                   p.tags->'addr:city'        AS city,
                   ST_X(ST_Centroid(ST_Transform(p.way, 4326))) AS lng,
                   ST_Y(ST_Centroid(ST_Transform(p.way, 4326))) AS lat,
                   ST_YMax(ST_Transform(ST_Envelope(p.way), 4326)) AS ne_lat,
                   ST_XMax(ST_Transform(ST_Envelope(p.way), 4326)) AS ne_lng,
                   ST_YMin(ST_Transform(ST_Envelope(p.way), 4326)) AS sw_lat,
                   ST_XMin(ST_Transform(ST_Envelope(p.way), 4326)) AS sw_lng,
                   p.tags->'opening_hours'   AS opening_hours,
                   nearest.name              AS fallback_street,
                   ST_Area(ST_Transform(p.way, 4326)::geography) AS area_m2
            FROM   planet_osm_polygon p
            LEFT JOIN LATERAL (
                SELECT name FROM planet_osm_line
                WHERE  highway IS NOT NULL AND name IS NOT NULL
                ORDER BY way <-> ST_Centroid(p.way)
                LIMIT  1
            ) nearest ON (p.tags->'addr:street') IS NULL
            WHERE  p.{column} = ANY(@values)
              AND  p.name IS NOT NULL
              AND  ST_DWithin(
                     ST_Transform(p.way, 4326)::geography,
                     ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography,
                     @radius)
            ORDER BY p.way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), 3857)
            LIMIT  {(openNow ? 40 : 10)}
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("values", values);
        cmd.Parameters.AddWithValue("lng", lng);
        cmd.Parameters.AddWithValue("lat", lat);
        cmd.Parameters.AddWithValue("radius", radiusMeters);

        return await ReadPlaceCandidates(cmd, hasViewport: true, openNow);
    }

    private static async Task<List<PlaceCandidate>> ReadPlaceCandidates(
        NpgsqlCommand cmd, bool hasViewport, bool openNow)
    {
        var results        = new List<PlaceCandidate>();
        var now            = DateTime.Now;
        int ohColumn       = hasViewport ? 11 : 7;
        int fallbackColumn = hasViewport ? 12 : 8;
        int areaColumn     = hasViewport ? 13 : -1; // only polygons have area

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var openingHours = reader.IsDBNull(ohColumn) ? null : reader.GetString(ohColumn);
            bool? isOpen     = openingHours != null ? IsOpenNow(openingHours, now) : null;

            // When openNow is requested, only keep places that have opening_hours data
            // AND are currently open. Places without opening_hours are excluded.
            if (openNow && isOpen != true)
                continue;

            var id             = reader.GetString(0);
            var name           = reader.GetString(1);
            var street         = reader.IsDBNull(2) ? null : reader.GetString(2);
            var housenumber    = reader.IsDBNull(3) ? null : reader.GetString(3);
            var city           = reader.IsDBNull(4) ? null : reader.GetString(4);
            var pLng           = reader.GetDouble(5);
            var pLat           = reader.GetDouble(6);
            var fallbackStreet = reader.IsDBNull(fallbackColumn) ? null : reader.GetString(fallbackColumn);
            var areaM2         = areaColumn >= 0 && !reader.IsDBNull(areaColumn) ? reader.GetDouble(areaColumn) : 0.0;

            var viewport = hasViewport
                ? (NeLat: reader.GetDouble(7), NeLng: reader.GetDouble(8),
                   SwLat: reader.GetDouble(9), SwLng: reader.GetDouble(10))
                : default;

            // If the POI has a real street address, use it. Otherwise fall back to
            // "POI Name, nearest street" so the user can orient themselves.
            var address = !string.IsNullOrEmpty(street)
                ? FormatAddress(null, street, housenumber, city)
                : !string.IsNullOrEmpty(fallbackStreet)
                    ? $"{name}, {fallbackStreet}"
                    : string.Empty;

            results.Add(new PlaceCandidate(
                PlaceId:          id,
                Name:             name,
                Address:          address,
                Rating:           0,
                UserRatingsTotal: 0,
                Latitude:         pLat,
                Longitude:        pLng,
                Viewport:         viewport,
                AreaM2:           areaM2,
                IsOpen:           isOpen));

            if (results.Count == 10) break;
        }

        return results;
    }

    // ── Autocomplete ───────────────────────────────────────────────────────────

    // Requires: CREATE EXTENSION IF NOT EXISTS pg_trgm;
    public async Task<List<PlaceSuggestion>> GetPlaceAutocompleteSuggestionsAsync(
        string input, double? lat, double? lng)
    {
        var q          = input.Trim();
        var pattern    = $"%{q}%";
        var startsWith = $"{q}%";
        var hasDigit   = q.Any(char.IsDigit);

        var cacheKey = AutocompleteCacheKey(q, lat, lng);
        if (_autocompleteCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Planar degree-distance squared — no geography cast, no function call,
        // perfectly sufficient for ordering within city bounds (~50 km radius).
        var proximitySelect = (lat.HasValue && lng.HasValue)
            ? "(sub.lng - @nearLng) * (sub.lng - @nearLng) + (sub.lat - @nearLat) * (sub.lat - @nearLat)"
            : "0::float8";

        // Bounding-box radius filter using the pre-computed near_pt CTE.
        // 20 000 EPSG:3857 units ≈ 13 km geographic at ~48° N — enough for city autocomplete.
        var radiusFilter = (lat.HasValue && lng.HasValue)
            ? "AND way && ST_Expand((SELECT geom FROM near_pt), 20000)"
            : "";

        // Inline SQL expression that formats a full address (street + housenumber + city)
        // from the OSM tags columns. Used in POI and area branches.
        const string addrExpr = """
            CASE
                WHEN (tags->'addr:street') IS NOT NULL THEN
                    (tags->'addr:street')
                    || CASE WHEN "addr:housenumber" IS NOT NULL THEN ' ' || "addr:housenumber" ELSE '' END
                    || CASE WHEN (tags->'addr:city') IS NOT NULL THEN ', ' || (tags->'addr:city') ELSE '' END
                ELSE (tags->'addr:city')
            END
            """;

        // Address nodes are included only when the input contains a digit (e.g. "Hauptstr 5").
        // Both point nodes and building polygons are checked, because Vienna OSM data stores
        // most building addresses on polygons, not standalone point nodes.
        // For address branches the name IS already "street housenumber", so address = just city.
        var addressUnion = hasDigit ? $"""

                UNION ALL

                -- Address nodes on points (street + housenumber)
                (
                    SELECT (tags->'addr:street') || ' ' || ("addr:housenumber") AS name,
                           tags->'addr:city' AS city,
                           ST_X(ST_Transform(way, 4326)) AS lng,
                           ST_Y(ST_Transform(way, 4326)) AS lat,
                           CASE WHEN (tags->'addr:street') || ' ' || ("addr:housenumber") ILIKE @startsWith THEN 0
                                WHEN (tags->'addr:street') || ' ' || ("addr:housenumber") ILIKE @pattern    THEN 1
                                ELSE 2 END AS mrank,
                           0 AS src,
                           tags->'addr:city' AS address
                    FROM   planet_osm_point
                    WHERE  (tags->'addr:street') IS NOT NULL
                      AND  ("addr:housenumber") IS NOT NULL
                      AND  ((tags->'addr:street') || ' ' || ("addr:housenumber")) ILIKE @pattern
                      {radiusFilter}
                    LIMIT  20
                )

                UNION ALL

                -- Address tags on building polygons (street + housenumber)
                (
                    SELECT (tags->'addr:street') || ' ' || ("addr:housenumber") AS name,
                           tags->'addr:city' AS city,
                           ST_X(ST_Centroid(ST_Transform(way, 4326))) AS lng,
                           ST_Y(ST_Centroid(ST_Transform(way, 4326))) AS lat,
                           CASE WHEN (tags->'addr:street') || ' ' || ("addr:housenumber") ILIKE @startsWith THEN 0
                                WHEN (tags->'addr:street') || ' ' || ("addr:housenumber") ILIKE @pattern    THEN 1
                                ELSE 2 END AS mrank,
                           0 AS src,
                           tags->'addr:city' AS address
                    FROM   planet_osm_polygon
                    WHERE  (tags->'addr:street') IS NOT NULL
                      AND  ("addr:housenumber") IS NOT NULL
                      AND  ((tags->'addr:street') || ' ' || ("addr:housenumber")) ILIKE @pattern
                      {radiusFilter}
                    LIMIT  20
                )
                """ : "";

        // mrank: 0 = prefix match, 1 = substring match, 2 = trigram-only (typo) match.
        // near_pt CTE pre-computes the EPSG:3857 user point once — referenced in every
        // ORDER BY and radiusFilter instead of repeating ST_Transform(ST_SetSRID(...)).
        // DISTINCT ON ORDER BY uses the `prox` alias to avoid recomputing proximitySelect.
        // Column order: name(0), city(1), lng(2), lat(3), address(4)
        var sql = $"""
            WITH near_pt AS (
                SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@nearLng, @nearLat), 4326), 3857) AS geom
            )
            SELECT name, city, lng, lat, address
            FROM (
                SELECT DISTINCT ON (lower(sub.name))
                       sub.name, sub.city, sub.lng, sub.lat, sub.mrank, sub.src, sub.address,
                       {proximitySelect} AS prox
                FROM (
                    -- Named POIs / places (point nodes), capped early
                    (
                        SELECT name,
                               tags->'addr:city' AS city,
                               ST_X(ST_Transform(way, 4326)) AS lng,
                               ST_Y(ST_Transform(way, 4326)) AS lat,
                               CASE WHEN name ILIKE @startsWith THEN 0
                                    WHEN name ILIKE @pattern    THEN 1
                                    ELSE                             2 END AS mrank,
                               0 AS src,
                               {addrExpr} AS address
                        FROM   planet_osm_point, near_pt
                        WHERE  (name ILIKE @pattern OR name % @q)
                          AND  name IS NOT NULL
                          {radiusFilter}
                        ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                      WHEN name ILIKE @pattern    THEN 1
                                      ELSE                             2 END,
                                 way <-> near_pt.geom
                        LIMIT  20
                    )

                    UNION ALL

                    -- Named streets / roads — one representative row per distinct name
                    (
                        SELECT name, city, lng, lat, mrank, src, address
                        FROM (
                            SELECT DISTINCT ON (name) name,
                                   NULL AS city,
                                   ST_X(ST_Transform(ST_Centroid(way), 4326)) AS lng,
                                   ST_Y(ST_Transform(ST_Centroid(way), 4326)) AS lat,
                                   CASE WHEN name ILIKE @startsWith THEN 0
                                        WHEN name ILIKE @pattern    THEN 1
                                        ELSE                             2 END AS mrank,
                                   1 AS src,
                                   NULL::text AS address
                            FROM   planet_osm_line, near_pt
                            WHERE  (name ILIKE @pattern OR name % @q)
                              AND  highway IS NOT NULL
                              {radiusFilter}
                            ORDER BY name,
                                     way <-> near_pt.geom
                            LIMIT  20
                        ) _streets
                    )

                    UNION ALL

                    -- Named areas (parks, districts, etc.), capped early
                    (
                        SELECT name,
                               tags->'addr:city' AS city,
                               ST_X(ST_Centroid(ST_Transform(way, 4326))) AS lng,
                               ST_Y(ST_Centroid(ST_Transform(way, 4326))) AS lat,
                               CASE WHEN name ILIKE @startsWith THEN 0
                                    WHEN name ILIKE @pattern    THEN 1
                                    ELSE                             2 END AS mrank,
                               0 AS src,
                               {addrExpr} AS address
                        FROM   planet_osm_polygon, near_pt
                        WHERE  (name ILIKE @pattern OR name % @q)
                          AND  (place IS NOT NULL OR leisure IS NOT NULL
                                OR landuse IS NOT NULL OR amenity IS NOT NULL)
                          {radiusFilter}
                        ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                      WHEN name ILIKE @pattern    THEN 1
                                      ELSE                             2 END,
                                 way <-> near_pt.geom
                        LIMIT  20
                    ){addressUnion}
                ) sub
                ORDER BY lower(sub.name), sub.mrank, sub.src, prox
            ) deduped
            ORDER BY mrank, src, prox
            LIMIT 5
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("pattern",    pattern);
        cmd.Parameters.AddWithValue("startsWith", startsWith);
        cmd.Parameters.AddWithValue("q",          q);
        // @nearLng/@nearLat serve as both the KNN anchor and the proximity/radius reference.
        // Falls back to Vienna centre when the caller has no location.
        cmd.Parameters.AddWithValue("nearLng", lng ?? 16.37);
        cmd.Parameters.AddWithValue("nearLat", lat ?? 48.21);

        var results = new List<PlaceSuggestion>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var name    = reader.GetString(0);
            var pLng    = reader.GetDouble(2);
            var pLat    = reader.GetDouble(3);
            var address = reader.IsDBNull(4) ? null : reader.GetString(4);

            results.Add(new PlaceSuggestion
            {
                Description = name,
                Address     = string.IsNullOrWhiteSpace(address) ? null : address,
                PlaceId     = $"{pLat.ToString(CultureInfo.InvariantCulture)},{pLng.ToString(CultureInfo.InvariantCulture)}"
            });
        }

        if (_autocompleteCache.Count >= AutocompleteCacheMaxSize)
            _autocompleteCache.Clear();
        _autocompleteCache[cacheKey] = results;

        return results;
    }

    // ── Navigation URL ─────────────────────────────────────────────────────────

    public string BuildNavigationUrl(
        double originLat, double originLng, string destination, List<WaypointInfo> waypoints)
    {
        var sb = new StringBuilder();
        sb.Append("https://www.google.com/maps/dir/?api=1");
        sb.Append($"&origin={originLat.ToString(CultureInfo.InvariantCulture)},{originLng.ToString(CultureInfo.InvariantCulture)}");
        sb.Append($"&destination={Uri.EscapeDataString(destination)}");

        if (waypoints.Count > 0)
        {
            var wps = string.Join("|", waypoints.Select(w =>
            {
                var name    = w.Name.Replace("|", " ").Trim();
                var address = w.Address.Replace("|", " ").Trim();
                return string.IsNullOrEmpty(address) ? name : $"{name}, {address}";
            }));
            sb.Append($"&waypoints={Uri.EscapeDataString(wps)}");
        }

        sb.Append("&travelmode=walking");
        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (string table, string column, string[] values) GetOsmFilter(string type) =>
        type.ToLowerInvariant() switch
        {
            "cafe" or "coffee"                   => ("planet_osm_point",   "amenity", ["cafe"]),
            "restaurant"                         => ("planet_osm_point",   "amenity", ["restaurant", "fast_food"]),
            "pharmacy" or "drugstore"            => ("planet_osm_point",   "amenity", ["pharmacy"]),
            "bar" or "pub"                       => ("planet_osm_point",   "amenity", ["bar", "pub"]),
            "library"                            => ("planet_osm_point",   "amenity", ["library"]),
            "bank"                               => ("planet_osm_point",   "amenity", ["bank"]),
            "atm"                                => ("planet_osm_point",   "amenity", ["atm"]),
            "bakery"                             => ("planet_osm_point",   "shop",    ["bakery"]),
            "supermarket" or "grocery"           => ("planet_osm_point",   "shop",    ["supermarket"]),
            "convenience_store" or "convenience" => ("planet_osm_point",   "shop",    ["convenience"]),
            "gym" or "fitness"                   => ("planet_osm_point",   "leisure", ["fitness_centre", "sports_centre"]),
            "museum"                             => ("planet_osm_point",   "tourism", ["museum"]),
            "park" or "parks"                    => ("planet_osm_polygon", "leisure", ["park", "garden"]),
            var t                                => ("planet_osm_point",   "amenity", [t])
        };

    // ── Opening hours parser ───────────────────────────────────────────────────
    // Handles the most common OSM opening_hours patterns:
    //   24/7 · off · Mo-Fr 09:00-18:00 · Mo,Sa 10:00-20:00 · 08:00-22:00
    //   multiple rules separated by ";" · overnight ranges (22:00-02:00)
    // Unknown / unparseable values → treated as open (unknown ≠ closed).

    // OSM day index: Mo=0 .. Su=6; maps from .NET DayOfWeek (Sun=0..Sat=6)
    private static readonly int[] DotNetToOsm = [6, 0, 1, 2, 3, 4, 5];
    private static readonly string[] OsmDays   = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    private static bool IsOpenNow(string openingHours, DateTime now)
    {
        var oh = openingHours.Trim();
        if (oh.Equals("24/7", StringComparison.OrdinalIgnoreCase)) return true;
        if (oh.Equals("off",  StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var rule in oh.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (EvaluateRule(rule, now)) return true;

        return false;
    }

    private static bool EvaluateRule(string rule, DateTime now)
    {
        if (rule.Equals("24/7", StringComparison.OrdinalIgnoreCase)) return true;
        if (rule.Equals("off",  StringComparison.OrdinalIgnoreCase)) return false;

        // Split into optional day-selector and time ranges: "Mo-Fr 09:00-18:00"
        var spaceIdx = rule.IndexOf(' ');
        string? dayPart  = null;
        string  timePart;

        if (spaceIdx > 0 && !rule[..spaceIdx].Contains(':'))
        {
            dayPart  = rule[..spaceIdx];
            timePart = rule[(spaceIdx + 1)..].Trim();
        }
        else
        {
            timePart = rule;
        }

        if (dayPart != null && !DayMatches(dayPart, now.DayOfWeek))
            return false;

        return TimeMatches(timePart, now.TimeOfDay);
    }

    private static bool DayMatches(string dayPart, DayOfWeek dow)
    {
        int osmNow = DotNetToOsm[(int)dow];
        foreach (var seg in dayPart.Split(',', StringSplitOptions.TrimEntries))
        {
            if (seg.Contains('-'))
            {
                var p = seg.Split('-');
                int from = Array.IndexOf(OsmDays, p[0].Trim());
                int to   = Array.IndexOf(OsmDays, p[1].Trim());
                if (from < 0 || to < 0) return true; // unparseable → don't filter
                bool inRange = from <= to
                    ? osmNow >= from && osmNow <= to
                    : osmNow >= from || osmNow <= to; // wraps (e.g. Sa-Mo)
                if (inRange) return true;
            }
            else
            {
                int idx = Array.IndexOf(OsmDays, seg.Trim());
                if (idx == osmNow) return true;
            }
        }
        return false;
    }

    private static bool TimeMatches(string timePart, TimeSpan current)
    {
        foreach (var range in timePart.Split(',', StringSplitOptions.TrimEntries))
        {
            var dash = range.LastIndexOf('-'); // use LastIndexOf to avoid matching minus in "24:00"
            if (dash <= 0) continue;
            if (!TimeSpan.TryParse(range[..dash].Trim(),       out var start)) continue;
            if (!TimeSpan.TryParse(range[(dash + 1)..].Trim(), out var end))   continue;

            bool open = end < start                         // overnight: 22:00-02:00
                ? current >= start || current < end
                : current >= start && current < end;
            if (open) return true;
        }
        return false;
    }

    private static string FormatAddress(string? name, string? street, string? housenumber, string? city)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(street))
            parts.Add(string.IsNullOrEmpty(housenumber) ? street : $"{street} {housenumber}");
        if (!string.IsNullOrEmpty(city))
            parts.Add(city);

        if (parts.Count == 0 && !string.IsNullOrEmpty(name))
            return name;

        return parts.Count > 0 ? string.Join(", ", parts) : name ?? string.Empty;
    }

    private async Task<string> ThrottledGetStringAsync(string url)
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
            if (elapsed < MinRequestIntervalMs)
                await Task.Delay((int)(MinRequestIntervalMs - elapsed));

            _apiLogger.LogRequest(url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await _httpClient.GetStringAsync(url);
                sw.Stop();
                _lastRequestTime = DateTime.UtcNow;
                _apiLogger.LogResponse(url, result, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _apiLogger.LogError(url, ex);
                throw;
            }
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private static List<(double Lat, double Lng)> DecodePolyline(string encoded)
    {
        var points = new List<(double, double)>();
        int index = 0, lat = 0, lng = 0;
        while (index < encoded.Length)
        {
            int shift = 0, result = 0, b;
            do { b = encoded[index++] - 63; result |= (b & 0x1f) << shift; shift += 5; } while (b >= 0x20);
            lat += (result & 1) != 0 ? ~(result >> 1) : result >> 1;
            shift = 0; result = 0;
            do { b = encoded[index++] - 63; result |= (b & 0x1f) << shift; shift += 5; } while (b >= 0x20);
            lng += (result & 1) != 0 ? ~(result >> 1) : result >> 1;
            points.Add((lat / 1e5, lng / 1e5));
        }
        return points;
    }

    // ── Google Directions response models ──────────────────────────────────────

    private class DirectionsApiResponse
    {
        public string Status { get; set; } = string.Empty;
        public DirectionsRoute[]? Routes { get; set; }
    }

    private class DirectionsRoute
    {
        [JsonPropertyName("overview_polyline")]
        public OverviewPolyline OverviewPolyline { get; set; } = new();
        public DirectionsLeg[]? Legs { get; set; }
    }

    private class OverviewPolyline
    {
        public string Points { get; set; } = string.Empty;
    }

    private class DirectionsLeg
    {
        [JsonPropertyName("end_address")]
        public string EndAddress { get; set; } = string.Empty;
    }
}
