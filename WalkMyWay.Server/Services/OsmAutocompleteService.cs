using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace WalkMyWay.Server.Services;

public class OsmAutocompleteService : IOsmAutocompleteService
{
    private readonly NpgsqlDataSource _db;

    private static readonly ConcurrentDictionary<string, List<PlaceSuggestion>> _cache = new();

    // Maps user-facing terms (German + English + common aliases) to OSM tag column + values.
    // Polygon = true → search planet_osm_polygon instead of planet_osm_point.
    private static readonly Dictionary<string, (string Column, string[] Values, bool Polygon)> CategoryMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bar"]           = ("amenity", ["bar", "pub"],                       false),
            ["bars"]          = ("amenity", ["bar", "pub"],                       false),
            ["pub"]           = ("amenity", ["bar", "pub"],                       false),
            ["pubs"]          = ("amenity", ["bar", "pub"],                       false),
            ["kneipe"]        = ("amenity", ["bar", "pub"],                       false),
            ["café"]          = ("amenity", ["cafe"],                             false),
            ["cafe"]          = ("amenity", ["cafe"],                             false),
            ["cafés"]         = ("amenity", ["cafe"],                             false),
            ["cafes"]         = ("amenity", ["cafe"],                             false),
            ["kaffee"]        = ("amenity", ["cafe"],                             false),
            ["kaffeehaus"]    = ("amenity", ["cafe"],                             false),
            ["coffeeshop"]    = ("amenity", ["cafe"],                             false),
            ["restaurant"]    = ("amenity", ["restaurant", "fast_food"],          false),
            ["restaurants"]   = ("amenity", ["restaurant", "fast_food"],          false),
            ["gasthaus"]      = ("amenity", ["restaurant", "fast_food"],          false),
            ["wirtshaus"]     = ("amenity", ["restaurant", "fast_food"],          false),
            ["fastfood"]      = ("amenity", ["fast_food"],                        false),
            ["apotheke"]      = ("amenity", ["pharmacy"],                         false),
            ["pharmacy"]      = ("amenity", ["pharmacy"],                         false),
            ["bank"]          = ("amenity", ["bank"],                             false),
            ["atm"]           = ("amenity", ["atm"],                              false),
            ["bankomat"]      = ("amenity", ["atm"],                              false),
            ["geldautomat"]   = ("amenity", ["atm"],                              false),
            ["bäckerei"]      = ("shop",    ["bakery"],                           false),
            ["bäcker"]        = ("shop",    ["bakery"],                           false),
            ["bakery"]        = ("shop",    ["bakery"],                           false),
            ["supermarkt"]    = ("shop",    ["supermarket"],                      false),
            ["supermarket"]   = ("shop",    ["supermarket"],                      false),
            ["lebensmittel"]  = ("shop",    ["supermarket", "convenience"],       false),
            ["grocery"]       = ("shop",    ["supermarket", "convenience"],       false),
            ["trafik"]        = ("shop",    ["tobacco"],                          false),
            ["convenience"]   = ("shop",    ["convenience"],                      false),
            ["gym"]           = ("leisure", ["fitness_centre", "sports_centre"],  false),
            ["fitness"]       = ("leisure", ["fitness_centre", "sports_centre"],  false),
            ["fitnessstudio"] = ("leisure", ["fitness_centre", "sports_centre"],  false),
            ["sportzentrum"]  = ("leisure", ["fitness_centre", "sports_centre"],  false),
            ["museum"]        = ("tourism", ["museum"],                           false),
            ["museen"]        = ("tourism", ["museum"],                           false),
            ["bibliothek"]    = ("amenity", ["library"],                          false),
            ["bücherei"]      = ("amenity", ["library"],                          false),
            ["library"]       = ("amenity", ["library"],                          false),
            ["park"]          = ("leisure", ["park", "garden"],                   true),
            ["parks"]         = ("leisure", ["park", "garden"],                   true),
            ["garten"]        = ("leisure", ["park", "garden"],                   true),
            ["spielplatz"]    = ("leisure", ["playground"],                       true),
            ["playground"]    = ("leisure", ["playground"],                       true),
            ["schule"]        = ("amenity", ["school"],                           false),
            ["school"]        = ("amenity", ["school"],                           false),
            ["krankenhaus"]   = ("amenity", ["hospital"],                         false),
            ["spital"]        = ("amenity", ["hospital"],                         false),
            ["hospital"]      = ("amenity", ["hospital"],                         false),
            ["arzt"]          = ("amenity", ["doctors"],                          false),
            ["doctor"]        = ("amenity", ["doctors"],                          false),
            ["zahnarzt"]      = ("amenity", ["dentist"],                          false),
            ["dentist"]       = ("amenity", ["dentist"],                          false),
            ["tankstelle"]    = ("amenity", ["fuel"],                             false),
            ["fuel"]          = ("amenity", ["fuel"],                             false),
            ["hotel"]         = ("tourism", ["hotel", "hostel", "guest_house"],   false),
            ["hostel"]        = ("tourism", ["hostel"],                           false),
            ["kirche"]        = ("amenity", ["place_of_worship"],                 false),
            ["church"]        = ("amenity", ["place_of_worship"],                 false),
            ["post"]          = ("amenity", ["post_office"],                      false),
            ["postamt"]       = ("amenity", ["post_office"],                      false),
            ["polizei"]       = ("amenity", ["police"],                           false),
            ["police"]        = ("amenity", ["police"],                           false),
            ["schwimmbad"]    = ("leisure", ["swimming_pool", "sports_centre"],   true),
            ["hallenbad"]     = ("leisure", ["swimming_pool", "sports_centre"],   true),
            ["kino"]          = ("amenity", ["cinema"],                           false),
            ["cinema"]        = ("amenity", ["cinema"],                           false),
            ["theater"]       = ("amenity", ["theatre"],                          false),
            ["theatre"]       = ("amenity", ["theatre"],                          false),
        };

    private static string CacheKey(string q, double? lat, double? lng) =>
        $"{q.ToLowerInvariant()}|{(lat.HasValue ? $"{lat:F2}" : "_")}|{(lng.HasValue ? $"{lng:F2}" : "_")}";

    public OsmAutocompleteService(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task<List<PlaceSuggestion>> GetSuggestionsAsync(string input, double? lat, double? lng)
    {
        if (!lat.HasValue || !lng.HasValue)
            throw new ArgumentException("lat and lng are required for autocomplete.");

        var q          = input.Trim();
        var pattern    = $"%{q}%";
        var startsWith = $"{q}%";
        var hasDigit   = q.Any(char.IsDigit);

        var cacheKey = CacheKey(q, lat, lng);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Detect whether input matches a known POI category (e.g. "bar", "café", "bäcker").
        var category = DetectCategory(q);

        // split "Hauptstraße 12a" → streetPart + housePart
        bool   hasAddressSplit = false;
        string streetPart = q, housePart = "", houseRegex = "";
        if (hasDigit)
        {
            var m = Regex.Match(q, @"^(.*?)\s+(\d+\S*)$");
            if (m.Success)
            {
                streetPart      = m.Groups[1].Value;
                housePart       = m.Groups[2].Value;
                houseRegex      = $"^{Regex.Escape(housePart)}([^0-9]|$)";
                hasAddressSplit = true;
            }
        }

        const string addrExpr = """
            CASE
                WHEN (tags->'addr:street') IS NOT NULL THEN
                    (tags->'addr:street')
                    || CASE WHEN "addr:housenumber" IS NOT NULL THEN ' ' || "addr:housenumber" ELSE '' END
                    || CASE WHEN (tags->'addr:city') IS NOT NULL THEN ', ' || (tags->'addr:city') ELSE '' END
                ELSE (tags->'addr:city')
            END
            """;

        const string plzCityExpr = """
            CASE
                WHEN (tags->'addr:postcode') IS NOT NULL AND (tags->'addr:city') IS NOT NULL
                    THEN (tags->'addr:postcode') || ' ' || (tags->'addr:city')
                WHEN (tags->'addr:postcode') IS NOT NULL THEN tags->'addr:postcode'
                ELSE tags->'addr:city'
            END
            """;

        // fuzzy street resolution via planet_osm_line.name (indexed) → exact IN-lookup on addr:street
        var fuzzyStreetsCte = hasAddressSplit ? $"""
            ,
            fuzzy_streets AS (
                SELECT name
                FROM (
                    SELECT DISTINCT name, similarity(name, @streetPart) AS sim
                    FROM   planet_osm_line, near_pt
                    WHERE  (name ILIKE @streetPattern OR name % @streetPart)
                      AND  highway IS NOT NULL
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                ) _s
                WHERE  sim > {AppConstants.FuzzyStreetSimilarityThreshold}
                ORDER BY sim DESC
                LIMIT  5
            )
            """ : "";

        var streetOnlyAddressUnion = !hasAddressSplit ? $"""

                UNION ALL

                -- address points for street-name-only search (numeric housenumber order)
                (
                    SELECT
                        (tags->'addr:street') || ' ' || "addr:housenumber"               AS name,
                        'address'::text                                                    AS result_type,
                        {plzCityExpr}                                                      AS display_address,
                        ST_X(ST_Transform(way, 4326))                                     AS lng,
                        ST_Y(ST_Transform(way, 4326))                                     AS lat,
                        2                                                                  AS type_rank,
                        0                                                                  AS mrank,
                        (regexp_match("addr:housenumber", '^([0-9]+)'))[1]::int           AS num_sort
                    FROM   planet_osm_point, near_pt
                    WHERE  (tags->'addr:street') ILIKE @q
                      AND  "addr:housenumber" IS NOT NULL
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    LIMIT  50
                )

                UNION ALL

                -- address polygons (buildings) for street-name-only search
                (
                    SELECT
                        (tags->'addr:street') || ' ' || "addr:housenumber"               AS name,
                        'address'::text                                                    AS result_type,
                        {plzCityExpr}                                                      AS display_address,
                        ST_X(ST_Centroid(ST_Transform(way, 4326)))                        AS lng,
                        ST_Y(ST_Centroid(ST_Transform(way, 4326)))                        AS lat,
                        2                                                                  AS type_rank,
                        0                                                                  AS mrank,
                        (regexp_match("addr:housenumber", '^([0-9]+)'))[1]::int           AS num_sort
                    FROM   planet_osm_polygon, near_pt
                    WHERE  (tags->'addr:street') ILIKE @q
                      AND  "addr:housenumber" IS NOT NULL
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    LIMIT  50
                )
                """ : "";

        var addressUnion = hasAddressSplit ? $"""

                UNION ALL

                -- address polygons (buildings — primary source in AT/Vienna OSM data)
                (
                    SELECT
                        (tags->'addr:street') || ' ' || "addr:housenumber"  AS name,
                        'address'::text                                      AS result_type,
                        {plzCityExpr}                                        AS display_address,
                        ST_X(ST_Centroid(ST_Transform(way, 4326)))           AS lng,
                        ST_Y(ST_Centroid(ST_Transform(way, 4326)))           AS lat,
                        0 AS type_rank,
                        CASE
                            WHEN (tags->'addr:street') ILIKE @streetExact
                                 AND "addr:housenumber" = @housePart                           THEN 0
                            WHEN (tags->'addr:street') ILIKE @streetPrefix
                                 AND "addr:housenumber" ~* ('^' || @housePart || '[^0-9]')    THEN 1
                            ELSE 2
                        END AS mrank,
                        (regexp_match("addr:housenumber", '^([0-9]+)'))[1]::int AS num_sort
                    FROM   planet_osm_polygon, near_pt
                    WHERE  (tags->'addr:street') IN (SELECT name FROM fuzzy_streets)
                      AND  "addr:housenumber" ~* @houseRegex
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    ORDER BY mrank, way <-> near_pt.geom
                    LIMIT  20
                )

                UNION ALL

                -- address points
                (
                    SELECT
                        (tags->'addr:street') || ' ' || "addr:housenumber"  AS name,
                        'address'::text                                      AS result_type,
                        {plzCityExpr}                                        AS display_address,
                        ST_X(ST_Transform(way, 4326))                        AS lng,
                        ST_Y(ST_Transform(way, 4326))                        AS lat,
                        0 AS type_rank,
                        CASE
                            WHEN (tags->'addr:street') ILIKE @streetExact
                                 AND "addr:housenumber" = @housePart                           THEN 0
                            WHEN (tags->'addr:street') ILIKE @streetPrefix
                                 AND "addr:housenumber" ~* ('^' || @housePart || '[^0-9]')    THEN 1
                            ELSE 2
                        END AS mrank,
                        (regexp_match("addr:housenumber", '^([0-9]+)'))[1]::int AS num_sort
                    FROM   planet_osm_point, near_pt
                    WHERE  (tags->'addr:street') IN (SELECT name FROM fuzzy_streets)
                      AND  "addr:housenumber" ~* @houseRegex
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    ORDER BY mrank, way <-> near_pt.geom
                    LIMIT  20
                )
                """ : "";

        var transitUnion = $"""

                UNION ALL

                -- transit stops (points)
                (
                    SELECT name,
                           'transit'::text AS result_type,
                           CASE
                               WHEN railway = 'subway_entrance'
                                    OR (railway IN ('station', 'halt') AND (tags->'station' = 'subway' OR tags->'subway' = 'yes'))
                                    OR tags->'subway' = 'yes'                          THEN 'U-Bahn'
                               WHEN railway IN ('station', 'halt')                     THEN 'Bahnhof'
                               WHEN railway = 'tram_stop' OR tags->'tram' = 'yes'     THEN 'Straßenbahn'
                               WHEN highway = 'bus_stop'  OR tags->'bus'  = 'yes'     THEN 'Bus'
                               ELSE 'Haltestelle'
                           END AS display_address,
                           ST_X(ST_Transform(way, 4326)) AS lng,
                           ST_Y(ST_Transform(way, 4326)) AS lat,
                           2 AS type_rank,
                           CASE WHEN name ILIKE @startsWith THEN 0
                                WHEN name ILIKE @pattern    THEN 1
                                ELSE                             2 END AS mrank,
                           NULL::int AS num_sort
                    FROM   planet_osm_point, near_pt
                    WHERE  (name ILIKE @pattern OR name % @q)
                      AND  name IS NOT NULL
                      AND  (
                               railway IN ('station', 'halt', 'tram_stop', 'subway_entrance')
                            OR highway = 'bus_stop'
                            OR tags->'subway' = 'yes'
                            OR tags->'tram'   = 'yes'
                            OR tags->'bus'    = 'yes'
                          )
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                  WHEN name ILIKE @pattern    THEN 1
                                  ELSE                             2 END,
                             way <-> near_pt.geom
                    LIMIT  20
                )

                UNION ALL

                -- transit station polygons (larger stations)
                (
                    SELECT name,
                           'transit'::text AS result_type,
                           CASE
                               WHEN tags->'station' = 'subway' OR tags->'subway' = 'yes' THEN 'U-Bahn'
                               WHEN railway IN ('station', 'halt')                        THEN 'Bahnhof'
                               ELSE 'Haltestelle'
                           END AS display_address,
                           ST_X(ST_Centroid(ST_Transform(way, 4326))) AS lng,
                           ST_Y(ST_Centroid(ST_Transform(way, 4326))) AS lat,
                           2 AS type_rank,
                           CASE WHEN name ILIKE @startsWith THEN 0
                                WHEN name ILIKE @pattern    THEN 1
                                ELSE                             2 END AS mrank,
                           NULL::int AS num_sort
                    FROM   planet_osm_polygon, near_pt
                    WHERE  (name ILIKE @pattern OR name % @q)
                      AND  name IS NOT NULL
                      AND  railway IN ('station', 'halt')
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                  WHEN name ILIKE @pattern    THEN 1
                                  ELSE                             2 END,
                             way <-> near_pt.geom
                    LIMIT  10
                )
                """;

        // Category-based POI union: when input matches a known type (e.g. "bar", "café", "bäcker"),
        // fetch the nearest POIs of that type sorted by proximity.
        // These get type_rank = -1 so they surface above all name-based results.
        var categoryUnion = "";
        if (category.HasValue)
        {
            var (catCol, _, catPolygon) = category.Value;
            categoryUnion = catPolygon
                ? $"""

                UNION ALL

                -- category POIs: polygon type (parks, playgrounds, pools …)
                (
                    SELECT name,
                           'poi'::text AS result_type,
                           {addrExpr}  AS display_address,
                           ST_X(ST_Centroid(ST_Transform(way, 4326))) AS lng,
                           ST_Y(ST_Centroid(ST_Transform(way, 4326))) AS lat,
                           -1 AS type_rank,
                           0  AS mrank,
                           NULL::int AS num_sort
                    FROM   planet_osm_polygon, near_pt
                    WHERE  {catCol} = ANY(@catValues)
                      AND  name IS NOT NULL
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    ORDER BY way <-> near_pt.geom
                    LIMIT  20
                )
                """
                : $"""

                UNION ALL

                -- category POIs: point type (bars, cafés, restaurants …)
                (
                    SELECT name,
                           'poi'::text AS result_type,
                           {addrExpr}  AS display_address,
                           ST_X(ST_Transform(way, 4326)) AS lng,
                           ST_Y(ST_Transform(way, 4326)) AS lat,
                           -1 AS type_rank,
                           0  AS mrank,
                           NULL::int AS num_sort
                    FROM   planet_osm_point, near_pt
                    WHERE  {catCol} = ANY(@catValues)
                      AND  name IS NOT NULL
                      AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                    ORDER BY way <-> near_pt.geom
                    LIMIT  20
                )
                """;
        }

        // column order in result: name(0), result_type(1), display_address(2), lng(3), lat(4)
        var sql = $"""
            WITH near_pt AS (
                SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@nearLng, @nearLat), 4326), 3857) AS geom
            ){fuzzyStreetsCte}
            SELECT
                r.name,
                r.result_type,
                CASE
                    WHEN r.result_type IN ('street', 'transit')
                        THEN street_loc.plz_city
                    WHEN r.result_type = 'poi' AND r.display_address IS NULL
                        THEN COALESCE(poi_fallback.nearest_street, street_loc.plz_city)
                    ELSE r.display_address
                END AS display_address,
                r.lng,
                r.lat
            FROM (
                SELECT DISTINCT ON (lower(sub.name), sub.result_type)
                       sub.name,
                       sub.result_type,
                       sub.display_address,
                       sub.lng,
                       sub.lat,
                       sub.type_rank,
                       sub.mrank,
                       sub.num_sort,
                       (sub.lng - @nearLng) * (sub.lng - @nearLng)
                         + (sub.lat - @nearLat) * (sub.lat - @nearLat) AS prox
                FROM (
                    -- named POIs: point nodes
                    (
                        SELECT name,
                               'poi'::text AS result_type,
                               {addrExpr}  AS display_address,
                               ST_X(ST_Transform(way, 4326)) AS lng,
                               ST_Y(ST_Transform(way, 4326)) AS lat,
                               3 AS type_rank,
                               CASE WHEN name ILIKE @startsWith THEN 0
                                    WHEN name ILIKE @pattern    THEN 1
                                    ELSE                             2 END AS mrank,
                               NULL::int AS num_sort
                        FROM   planet_osm_point, near_pt
                        WHERE  (name ILIKE @pattern OR name % @q)
                          AND  name IS NOT NULL
                          AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                        ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                      WHEN name ILIKE @pattern    THEN 1
                                      ELSE                             2 END,
                                 way <-> near_pt.geom
                        LIMIT  20
                    )

                    UNION ALL

                    -- named streets / roads — nearest segment per distinct name
                    (
                        SELECT name, result_type, display_address, lng, lat, type_rank, mrank, num_sort
                        FROM (
                            SELECT DISTINCT ON (name) name,
                                   'street'::text  AS result_type,
                                   NULL::text      AS display_address,
                                   ST_X(ST_Transform(ST_Centroid(way), 4326)) AS lng,
                                   ST_Y(ST_Transform(ST_Centroid(way), 4326)) AS lat,
                                   1               AS type_rank,
                                   CASE WHEN name ILIKE @startsWith THEN 0
                                        WHEN name ILIKE @pattern    THEN 1
                                        ELSE                             2 END AS mrank,
                                   NULL::int AS num_sort
                            FROM   planet_osm_line, near_pt
                            WHERE  (name ILIKE @pattern OR name % @q)
                              AND  highway IS NOT NULL
                              AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                            ORDER BY name, way <-> near_pt.geom
                        ) _streets
                        ORDER BY mrank,
                                 (lng - @nearLng) * (lng - @nearLng) + (lat - @nearLat) * (lat - @nearLat)
                        LIMIT 20
                    )

                    UNION ALL

                    -- named areas (parks, districts, etc.)
                    (
                        SELECT name,
                               'poi'::text AS result_type,
                               {addrExpr}  AS display_address,
                               ST_X(ST_Centroid(ST_Transform(way, 4326))) AS lng,
                               ST_Y(ST_Centroid(ST_Transform(way, 4326))) AS lat,
                               3 AS type_rank,
                               CASE WHEN name ILIKE @startsWith THEN 0
                                    WHEN name ILIKE @pattern    THEN 1
                                    ELSE                             2 END AS mrank,
                               NULL::int AS num_sort
                        FROM   planet_osm_polygon, near_pt
                        WHERE  (name ILIKE @pattern OR name % @q)
                          AND  (place IS NOT NULL OR leisure IS NOT NULL
                                OR landuse IS NOT NULL OR amenity IS NOT NULL)
                          AND  way && ST_Expand(near_pt.geom, {AppConstants.AutocompleteSearchRadiusM})
                        ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                      WHEN name ILIKE @pattern    THEN 1
                                      ELSE                             2 END,
                                 way <-> near_pt.geom
                        LIMIT  20
                    ){transitUnion}{addressUnion}{streetOnlyAddressUnion}{categoryUnion}
                ) sub
                ORDER BY lower(sub.name), sub.result_type, sub.type_rank, sub.mrank, sub.num_sort NULLS LAST, prox
            ) r

            -- plz + city for streets via nearest address point
            LEFT JOIN LATERAL (
                SELECT {plzCityExpr} AS plz_city
                FROM   planet_osm_point
                WHERE  ((tags->'addr:postcode') IS NOT NULL OR (tags->'addr:city') IS NOT NULL)
                  AND  way && ST_Expand(
                           ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857), {AppConstants.AutocompleteFallbackRadiusM})
                ORDER BY way <-> ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857)
                LIMIT  1
            ) street_loc ON (r.result_type IN ('street', 'transit', 'poi'))

            -- nearest street name for POIs without address
            LEFT JOIN LATERAL (
                SELECT l.name AS nearest_street
                FROM   planet_osm_line l
                WHERE  l.highway IS NOT NULL AND l.name IS NOT NULL
                  AND  l.way && ST_Expand(
                           ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857), {AppConstants.AutocompleteFallbackRadiusM})
                ORDER BY l.way <-> ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857)
                LIMIT  1
            ) poi_fallback ON (r.result_type = 'poi' AND r.display_address IS NULL)

            ORDER BY r.prox, r.type_rank, r.mrank, r.num_sort NULLS LAST
            LIMIT {AppConstants.AutocompleteMaxResults}
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("pattern",    pattern);
        cmd.Parameters.AddWithValue("startsWith", startsWith);
        cmd.Parameters.AddWithValue("q",          q);
        cmd.Parameters.AddWithValue("nearLng",    lng.Value);
        cmd.Parameters.AddWithValue("nearLat",    lat.Value);

        if (hasAddressSplit)
        {
            cmd.Parameters.AddWithValue("streetPart",    streetPart);
            cmd.Parameters.AddWithValue("streetPattern", $"%{streetPart}%");
            cmd.Parameters.AddWithValue("streetExact",   streetPart);
            cmd.Parameters.AddWithValue("streetPrefix",  $"{streetPart}%");
            cmd.Parameters.AddWithValue("housePart",     housePart);
            cmd.Parameters.AddWithValue("houseRegex",    houseRegex);
        }

        if (category.HasValue)
            cmd.Parameters.AddWithValue("catValues", category.Value.Values);

        var results = new List<PlaceSuggestion>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var name       = reader.GetString(0);
            var resultType = reader.IsDBNull(1) ? null : reader.GetString(1);
            var address    = reader.IsDBNull(2) ? null : reader.GetString(2);
            var pLng       = reader.GetDouble(3);
            var pLat       = reader.GetDouble(4);

            results.Add(new PlaceSuggestion
            {
                Description = name,
                Address     = string.IsNullOrWhiteSpace(address) ? null : address,
                PlaceId     = $"{pLat.ToString(CultureInfo.InvariantCulture)},{pLng.ToString(CultureInfo.InvariantCulture)}",
                ResultType  = resultType,
                DistanceKm  = Math.Round(HaversineKm(lat.Value, lng.Value, pLat, pLng), 2)
            });
        }

        if (_cache.Count >= AppConstants.AutocompleteCacheMaxSize)
            _cache.Clear();
        _cache[cacheKey] = results;

        return results;
    }

    // Returns the OSM category that best matches the user's input.
    // Requires at least 3 characters. Tries exact match first, then
    // accent-normalized prefix match (e.g. "bäck" → bäckerei, "cafe" → café).
    private static (string Column, string[] Values, bool Polygon)? DetectCategory(string q)
    {
        if (q.Length < AppConstants.AutocompleteCategoryMinLength) return null;

        if (CategoryMap.TryGetValue(q, out var exact)) return exact;

        var normalized = NormalizeAccents(q.ToLowerInvariant());

        // Prefer the shortest matching key so "res" → "restaurant" beats a longer alias.
        (string Column, string[] Values, bool Polygon)? best = null;
        var bestLen = int.MaxValue;

        foreach (var (key, val) in CategoryMap)
        {
            if (key.Length >= bestLen) continue;
            if (NormalizeAccents(key).StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                best    = val;
                bestLen = key.Length;
            }
        }

        return best;
    }

    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    // Strips diacritics so "cafe" matches "café", "backer" matches "bäcker", etc.
    private static string NormalizeAccents(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }
}
