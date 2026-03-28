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
        // 1. Try nearest address within 100 m — checks both point nodes and building polygons
        //    because OSM Vienna data stores most addresses as polygon tags, not standalone points.
        const string addrSql = """
            SELECT street, housenumber, city
            FROM (
                SELECT tags->'addr:street'      AS street,
                       "addr:housenumber" AS housenumber,
                       tags->'addr:city'        AS city,
                       way
                FROM   planet_osm_point
                WHERE  (tags->'addr:street')      IS NOT NULL
                  AND  ("addr:housenumber") IS NOT NULL
                UNION ALL
                SELECT tags->'addr:street'      AS street,
                       "addr:housenumber" AS housenumber,
                       tags->'addr:city'        AS city,
                       ST_Centroid(way)         AS way
                FROM   planet_osm_polygon
                WHERE  (tags->'addr:street')      IS NOT NULL
                  AND  ("addr:housenumber") IS NOT NULL
            ) addr
            WHERE ST_DWithin(
                    ST_Transform(way, 4326)::geography,
                    ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography,
                    100)
            ORDER BY way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), 3857)
            LIMIT  1
            """;

        await using (var cmd = _db.CreateCommand(addrSql))
        {
            cmd.Parameters.AddWithValue("lng", lng);
            cmd.Parameters.AddWithValue("lat", lat);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                var street = r.IsDBNull(0) ? null : r.GetString(0);
                var nr     = r.IsDBNull(1) ? null : r.GetString(1);
                var city   = r.IsDBNull(2) ? null : r.GetString(2);
                var addr   = FormatAddress(null, street, nr, city);
                if (!string.IsNullOrEmpty(addr)) return addr;
            }
        }

        // 2. Fall back to nearest street name (more useful than a random named POI)
        const string streetSql = """
            SELECT name
            FROM   planet_osm_line
            WHERE  name IS NOT NULL
              AND  highway IS NOT NULL
            ORDER BY way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), 3857)
            LIMIT  1
            """;

        await using (var cmd = _db.CreateCommand(streetSql))
        {
            cmd.Parameters.AddWithValue("lng", lng);
            cmd.Parameters.AddWithValue("lat", lat);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                var street = r.IsDBNull(0) ? null : r.GetString(0);
                if (!string.IsNullOrEmpty(street)) return street;
            }
        }

        return $"{lat:F6}, {lng:F6}";
    }

    // ── Walking route — Google Maps Directions API ─────────────────────────────

    public async Task<RouteData> GetWalkingRouteAsync(double originLat, double originLng, string destination)
    {
        var url = $"https://maps.googleapis.com/maps/api/directions/json" +
                  $"?origin={originLat.ToString(CultureInfo.InvariantCulture)},{originLng.ToString(CultureInfo.InvariantCulture)}" +
                  $"&destination={Uri.EscapeDataString(destination)}" +
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
        // openNow: OSM has no real-time open/closed data — parameter is silently ignored.
        var (table, column, values) = GetOsmFilter(type);

        if (table == "planet_osm_polygon")
            return await SearchPolygonsNearbyAsync(lat, lng, radiusMeters, column, values);

        return await SearchPointsNearbyAsync(lat, lng, radiusMeters, column, values);
    }

    private async Task<List<PlaceCandidate>> SearchPointsNearbyAsync(
        double lat, double lng, int radiusMeters, string column, string[] values)
    {
        var sql = $"""
            SELECT osm_id::text,
                   name,
                   tags->'addr:street'      AS street,
                   "addr:housenumber" AS housenumber,
                   tags->'addr:city'        AS city,
                   ST_X(ST_Transform(way, 4326)) AS lng,
                   ST_Y(ST_Transform(way, 4326)) AS lat
            FROM   planet_osm_point
            WHERE  {column} = ANY(@values)
              AND  name IS NOT NULL
              AND  ST_DWithin(
                     ST_Transform(way, 4326)::geography,
                     ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography,
                     @radius)
            ORDER BY way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), 3857)
            LIMIT  10
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("values", values);
        cmd.Parameters.AddWithValue("lng", lng);
        cmd.Parameters.AddWithValue("lat", lat);
        cmd.Parameters.AddWithValue("radius", radiusMeters);

        return await ReadPlaceCandidates(cmd, hasViewport: false);
    }

    private async Task<List<PlaceCandidate>> SearchPolygonsNearbyAsync(
        double lat, double lng, int radiusMeters, string column, string[] values)
    {
        var sql = $"""
            SELECT osm_id::text,
                   name,
                   tags->'addr:street'      AS street,
                   "addr:housenumber" AS housenumber,
                   tags->'addr:city'        AS city,
                   ST_X(ST_Centroid(ST_Transform(way, 4326))) AS lng,
                   ST_Y(ST_Centroid(ST_Transform(way, 4326))) AS lat,
                   ST_YMax(ST_Transform(ST_Envelope(way), 4326)) AS ne_lat,
                   ST_XMax(ST_Transform(ST_Envelope(way), 4326)) AS ne_lng,
                   ST_YMin(ST_Transform(ST_Envelope(way), 4326)) AS sw_lat,
                   ST_XMin(ST_Transform(ST_Envelope(way), 4326)) AS sw_lng
            FROM   planet_osm_polygon
            WHERE  {column} = ANY(@values)
              AND  name IS NOT NULL
              AND  ST_DWithin(
                     ST_Transform(way, 4326)::geography,
                     ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography,
                     @radius)
            ORDER BY way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@lng, @lat), 4326), 3857)
            LIMIT  10
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("values", values);
        cmd.Parameters.AddWithValue("lng", lng);
        cmd.Parameters.AddWithValue("lat", lat);
        cmd.Parameters.AddWithValue("radius", radiusMeters);

        return await ReadPlaceCandidates(cmd, hasViewport: true);
    }

    private static async Task<List<PlaceCandidate>> ReadPlaceCandidates(NpgsqlCommand cmd, bool hasViewport)
    {
        var results = new List<PlaceCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var id          = reader.GetString(0);
            var name        = reader.GetString(1);
            var street      = reader.IsDBNull(2) ? null : reader.GetString(2);
            var housenumber = reader.IsDBNull(3) ? null : reader.GetString(3);
            var city        = reader.IsDBNull(4) ? null : reader.GetString(4);
            var pLng        = reader.GetDouble(5);
            var pLat        = reader.GetDouble(6);

            var viewport = hasViewport
                ? (NeLat: reader.GetDouble(7), NeLng: reader.GetDouble(8),
                   SwLat: reader.GetDouble(9), SwLng: reader.GetDouble(10))
                : default;

            var address = FormatAddress(null, street, housenumber, city);

            results.Add(new PlaceCandidate(
                PlaceId:          id,
                Name:             name,
                Address:          address,
                Rating:           0,    // OSM has no ratings
                UserRatingsTotal: 0,
                Latitude:         pLat,
                Longitude:        pLng,
                Viewport:         viewport));
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

        var proximitySelect = (lat.HasValue && lng.HasValue)
            ? "ST_Distance(ST_SetSRID(ST_MakePoint(sub.lng, sub.lat), 4326)::geography, ST_SetSRID(ST_MakePoint(@userLng, @userLat), 4326)::geography)"
            : "0";

        // Spatial filter in native EPSG:3857 — avoids per-row geography cast and lets
        // PostgreSQL use the GiST index on `way` directly.
        // 20 000 Cartesian units ≈ 13 km geographic at ~48° N latitude, which is
        // more than enough for pedestrian-route autocomplete within a city.
        var radiusFilter = (lat.HasValue && lng.HasValue)
            ? """
              AND ST_DWithin(
                    way,
                    ST_Transform(ST_SetSRID(ST_MakePoint(@userLng, @userLat), 4326), 3857),
                    20000)
              """
            : "";

        // Address nodes are included only when the input contains a digit (e.g. "Hauptstr 5").
        // Both point nodes and building polygons are checked, because Vienna OSM data stores
        // most building addresses on polygons, not standalone point nodes.
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
                           0 AS src
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
                           0 AS src
                    FROM   planet_osm_polygon
                    WHERE  (tags->'addr:street') IS NOT NULL
                      AND  ("addr:housenumber") IS NOT NULL
                      AND  ((tags->'addr:street') || ' ' || ("addr:housenumber")) ILIKE @pattern
                      {radiusFilter}
                    LIMIT  20
                )
                """ : "";

        // mrank: 0 = prefix match, 1 = substring match, 2 = trigram-only (typo) match.
        // Each branch is capped with LIMIT 20 before the outer dedup to avoid scanning
        // more rows than necessary. DISTINCT ON then picks the best match per name.
        var sql = $"""
            SELECT name, city, lng, lat
            FROM (
                SELECT DISTINCT ON (lower(sub.name))
                       sub.name, sub.city, sub.lng, sub.lat, sub.mrank, sub.src,
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
                               0 AS src
                        FROM   planet_osm_point
                        WHERE  (name ILIKE @pattern OR name % @q)
                          AND  name IS NOT NULL
                          {radiusFilter}
                        ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                      WHEN name ILIKE @pattern    THEN 1
                                      ELSE                             2 END,
                                 way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@nearLng, @nearLat), 4326), 3857)
                        LIMIT  20
                    )

                    UNION ALL

                    -- Named streets / roads — one representative row per distinct name
                    (
                        SELECT name, city, lng, lat, mrank, src
                        FROM (
                            SELECT DISTINCT ON (name) name,
                                   NULL AS city,
                                   ST_X(ST_Transform(ST_Centroid(way), 4326)) AS lng,
                                   ST_Y(ST_Transform(ST_Centroid(way), 4326)) AS lat,
                                   CASE WHEN name ILIKE @startsWith THEN 0
                                        WHEN name ILIKE @pattern    THEN 1
                                        ELSE                             2 END AS mrank,
                                   1 AS src
                            FROM   planet_osm_line
                            WHERE  (name ILIKE @pattern OR name % @q)
                              AND  highway IS NOT NULL
                              {radiusFilter}
                            ORDER BY name,
                                     way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@nearLng, @nearLat), 4326), 3857)
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
                               0 AS src
                        FROM   planet_osm_polygon
                        WHERE  (name ILIKE @pattern OR name % @q)
                          AND  (place IS NOT NULL OR leisure IS NOT NULL
                                OR landuse IS NOT NULL OR amenity IS NOT NULL)
                          {radiusFilter}
                        ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                      WHEN name ILIKE @pattern    THEN 1
                                      ELSE                             2 END,
                                 way <-> ST_Transform(ST_SetSRID(ST_MakePoint(@nearLng, @nearLat), 4326), 3857)
                        LIMIT  20
                    ){addressUnion}
                ) sub
                ORDER BY lower(sub.name), sub.mrank, sub.src, {proximitySelect}
            ) deduped
            ORDER BY mrank, src, prox
            LIMIT 5
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("pattern", pattern);
        cmd.Parameters.AddWithValue("startsWith", startsWith);
        cmd.Parameters.AddWithValue("q", q);
        // nearLng/nearLat used for street dedup ordering — fall back to Vienna centre
        cmd.Parameters.AddWithValue("nearLng", lng ?? 16.37);
        cmd.Parameters.AddWithValue("nearLat", lat ?? 48.21);
        if (lat.HasValue && lng.HasValue)
        {
            cmd.Parameters.AddWithValue("userLat", lat.Value);
            cmd.Parameters.AddWithValue("userLng", lng.Value);
        }

        var results = new List<PlaceSuggestion>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var city = reader.IsDBNull(1) ? null : reader.GetString(1);
            var pLng = reader.GetDouble(2);
            var pLat = reader.GetDouble(3);

            results.Add(new PlaceSuggestion
            {
                Description = string.IsNullOrEmpty(city) ? name : $"{name}, {city}",
                PlaceId     = $"{pLat.ToString(CultureInfo.InvariantCulture)},{pLng.ToString(CultureInfo.InvariantCulture)}"
            });
        }

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
