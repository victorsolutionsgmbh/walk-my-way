using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace WalkMyWay.Server.Services;

public class OsmAutocompleteService : IOsmAutocompleteService
{
    private readonly NpgsqlDataSource _db;

    private static readonly ConcurrentDictionary<string, List<PlaceSuggestion>> _cache = new();
    private const int CacheMaxSize = 200;

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
                      AND  way && ST_Expand(near_pt.geom, 20000)
                ) _s
                WHERE  sim > 0.45
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
                      AND  way && ST_Expand(near_pt.geom, 20000)
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
                      AND  way && ST_Expand(near_pt.geom, 20000)
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
                      AND  way && ST_Expand(near_pt.geom, 20000)
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
                      AND  way && ST_Expand(near_pt.geom, 20000)
                    ORDER BY mrank, way <-> near_pt.geom
                    LIMIT  20
                )
                """ : "";

        // transit stops: points (u-bahn entrances, tram stops, bus stops) + station polygons
        const string transitUnion = """

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
                      AND  way && ST_Expand(near_pt.geom, 20000)
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
                      AND  way && ST_Expand(near_pt.geom, 20000)
                    ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                  WHEN name ILIKE @pattern    THEN 1
                                  ELSE                             2 END,
                             way <-> near_pt.geom
                    LIMIT  10
                )
                """;

        // column order in result: name(0), result_type(1), display_address(2), lng(3), lat(4)
        var sql = $"""
            WITH near_pt AS (
                SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@nearLng, @nearLat), 4326), 3857) AS geom
            ){fuzzyStreetsCte}
            SELECT
                r.name,
                r.result_type,
                CASE
                    WHEN r.result_type = 'street'
                        THEN street_loc.plz_city
                    WHEN r.result_type = 'poi' AND r.display_address IS NULL
                        THEN poi_fallback.nearest_street
                    ELSE r.display_address
                END AS display_address,
                r.lng,
                r.lat
            FROM (
                SELECT DISTINCT ON (lower(sub.name))
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
                          AND  way && ST_Expand(near_pt.geom, 20000)
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
                              AND  way && ST_Expand(near_pt.geom, 20000)
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
                          AND  way && ST_Expand(near_pt.geom, 20000)
                        ORDER BY CASE WHEN name ILIKE @startsWith THEN 0
                                      WHEN name ILIKE @pattern    THEN 1
                                      ELSE                             2 END,
                                 way <-> near_pt.geom
                        LIMIT  20
                    ){transitUnion}{addressUnion}{streetOnlyAddressUnion}
                ) sub
                ORDER BY lower(sub.name), sub.type_rank, sub.mrank, sub.num_sort NULLS LAST, prox
            ) r

            -- plz + city for streets via nearest address point
            LEFT JOIN LATERAL (
                SELECT {plzCityExpr} AS plz_city
                FROM   planet_osm_point
                WHERE  ((tags->'addr:postcode') IS NOT NULL OR (tags->'addr:city') IS NOT NULL)
                  AND  way && ST_Expand(
                           ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857), 5000)
                ORDER BY way <-> ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857)
                LIMIT  1
            ) street_loc ON (r.result_type = 'street')

            -- nearest street name for POIs without address
            LEFT JOIN LATERAL (
                SELECT l.name AS nearest_street
                FROM   planet_osm_line l
                WHERE  l.highway IS NOT NULL AND l.name IS NOT NULL
                  AND  l.way && ST_Expand(
                           ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857), 5000)
                ORDER BY l.way <-> ST_Transform(ST_SetSRID(ST_MakePoint(r.lng, r.lat), 4326), 3857)
                LIMIT  1
            ) poi_fallback ON (r.result_type = 'poi' AND r.display_address IS NULL)

            ORDER BY r.type_rank, r.mrank, r.num_sort NULLS LAST, r.prox
            LIMIT 5
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
                ResultType  = resultType
            });
        }

        if (_cache.Count >= CacheMaxSize)
            _cache.Clear();
        _cache[cacheKey] = results;

        return results;
    }
}
