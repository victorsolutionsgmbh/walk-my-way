using WalkMyWay.Server;
using WalkMyWay.Server.Models;

namespace WalkMyWay.Server.Services;

public class RouteCalculationService : IRouteCalculationService
{
    private readonly IMapProvider _mapProvider;

    public RouteCalculationService(IMapProvider mapProvider)
    {
        _mapProvider = mapProvider;
    }

    public async Task<RouteResponse> GetWalkingRouteAsync(RouteRequest request)
    {
        var routeData = await _mapProvider.GetWalkingRouteAsync(
            request.CurrentLatitude, request.CurrentLongitude, request.DestinationAddress);

        var routePoints = routeData.Points;
        var waypoints = new List<WaypointInfo>();

        if (request.PreserveOrder)
        {
            var prefs = request.Preferences;

            // one search per unique type with summed count + buffer;
            // repeated preferences of the same type draw from the same pool so no candidates are missed
            var uniqueTypes = prefs
                .GroupBy(p => p.Type)
                .Select(g => (Type: g.Key, TotalCount: g.Sum(p => p.Count)))
                .ToList();

            var typeSearches = await Task.WhenAll(
                uniqueTypes.Select(t => FindPlacesAlongRouteAsync(routePoints, t.Type, t.TotalCount + 4, false)));

            // sort by route position so the greedy loop picks the earliest valid stop first,
            // leaving as much room as possible for subsequent preferences
            var typeCandidates = new Dictionary<string, List<WaypointInfo>>();
            for (int j = 0; j < uniqueTypes.Count; j++)
                typeCandidates[uniqueTypes[j].Type] = typeSearches[j]
                    .OrderBy(wp => ClosestRouteIndex(wp.Latitude, wp.Longitude, routePoints))
                    .ThenBy(wp => wp.PlaceId)
                    .ToList();

            // greedy assignment: each next stop must be at or after the previous stop on the route;
            // exception: if a stop is within BacktrackThresholdM of the current route position
            // it is placed anyway and flagged as a backtrack
            var seenPlaceIds = new HashSet<string>();
            int minRouteIdx  = 0;

            for (int i = 0; i < prefs.Count; i++)
            {
                var pool     = typeCandidates[prefs[i].Type];
                var openNow  = prefs[i].OpenNow;
                int needed   = prefs[i].Count;
                int taken    = 0;

                // pass 1: forward candidates only (no backtrack)
                foreach (var wp in pool)
                {
                    if (taken >= needed) break;
                    if (openNow && wp.IsOpen != true) continue;
                    var idx = ClosestRouteIndex(wp.Latitude, wp.Longitude, routePoints);
                    if (idx >= minRouteIdx && seenPlaceIds.Add(wp.PlaceId))
                    {
                        waypoints.Add(wp);
                        minRouteIdx = idx;
                        taken++;
                    }
                }

                if (taken >= needed) continue;

                // pass 2: allow nearby backtrack candidates only when no forward stop was found
                foreach (var wp in pool)
                {
                    if (taken >= needed) break;
                    if (openNow && wp.IsOpen != true) continue;
                    if (seenPlaceIds.Contains(wp.PlaceId)) continue;
                    var idx  = ClosestRouteIndex(wp.Latitude, wp.Longitude, routePoints);
                    if (idx >= minRouteIdx) continue; // already handled in pass 1
                    var dist = HaversineDistance(
                        wp.Latitude, wp.Longitude,
                        routePoints[minRouteIdx].Lat, routePoints[minRouteIdx].Lng);
                    if (dist > AppConstants.BacktrackThresholdM) continue;
                    if (seenPlaceIds.Add(wp.PlaceId))
                    {
                        wp.BacktrackRequired = true;
                        waypoints.Add(wp);
                        minRouteIdx = Math.Max(minRouteIdx, idx);
                        taken++;
                    }
                }
            }
        }
        else
        {
            // one search per unique type with summed count; same fix as preserveOrder path
            var uniqueTypes = request.Preferences
                .GroupBy(p => p.Type)
                .Select(g => (Type: g.Key, TotalCount: g.Sum(p => p.Count), OpenNow: g.Any(p => p.OpenNow)))
                .ToList();

            var typeSearches = await Task.WhenAll(
                uniqueTypes.Select(t => FindPlacesAlongRouteAsync(routePoints, t.Type, t.TotalCount, t.OpenNow)));

            var seenPlaceIds = new HashSet<string>();
            foreach (var places in typeSearches)
                foreach (var place in places)
                    if (seenPlaceIds.Add(place.PlaceId))
                        waypoints.Add(place);

            waypoints = SortWaypointsByRouteOrder(waypoints, routePoints);
        }

        waypoints = waypoints.Take(AppConstants.MaxStops).ToList();

        return new RouteResponse
        {
            GoogleMapsUrl = _mapProvider.BuildNavigationUrl(
                request.CurrentLatitude, request.CurrentLongitude,
                request.DestinationAddress, waypoints),
            DestinationAddress = routeData.EndAddress,
            Waypoints          = waypoints,
            HasBacktracks      = waypoints.Any(w => w.BacktrackRequired)
        };
    }

    // radius and max route-distance tolerance per attempt; each retry widens the search
    private static readonly (int Radius, int RouteDistMax)[] SearchAttempts =
        [(800, 250), (1400, 400), (2200, 800)];

    private const int AlternativesRadiusM = 400;
    private const int MaxAlternatives     = 3;

    private async Task<List<WaypointInfo>> FindPlacesAlongRouteAsync(
        List<(double Lat, double Lng)> routePoints, string preferenceType, int maxCount, bool openNow)
    {
        foreach (var (radius, routeDistMax) in SearchAttempts)
        {
            var result = await FindPlacesAttemptAsync(routePoints, preferenceType, maxCount, openNow, radius, routeDistMax);
            if (result.Count > 0) return result;
        }
        return [];
    }

    private async Task<List<WaypointInfo>> FindPlacesAttemptAsync(
        List<(double Lat, double Lng)> routePoints, string preferenceType, int maxCount, bool openNow,
        int searchRadius, int routeDistMax)
    {
        var found        = new Dictionary<string, (WaypointInfo Info, (double NeLat, double NeLng, double SwLat, double SwLng) Viewport, double AreaM2)>();
        var samplePoints = GetSampledPoints(routePoints, 5);

        var candidateLists = await Task.WhenAll(
            samplePoints.Select(p => _mapProvider.SearchPlacesNearbyAsync(p.Lat, p.Lng, searchRadius, preferenceType, openNow)));

        foreach (var candidates in candidateLists)
            foreach (var c in candidates)
                if (!found.ContainsKey(c.PlaceId))
                    found[c.PlaceId] = (new WaypointInfo
                    {
                        PlaceId          = c.PlaceId,
                        Name             = c.Name,
                        Type             = preferenceType,
                        Address          = c.Address,
                        Rating           = c.Rating,
                        UserRatingsTotal = c.UserRatingsTotal,
                        Latitude         = c.Latitude,
                        Longitude        = c.Longitude,
                        IsOpen           = c.IsOpen
                    }, c.Viewport, c.AreaM2);

        // for parks prefer larger polygons; thenby placeid ensures deterministic ordering on equal score
        var scored = found.Values
            .Select(entry =>
            {
                var routeDist  = MinRouteDistanceBounded(
                    entry.Info.Latitude, entry.Info.Longitude, entry.Viewport, routePoints);
                var areaFactor = preferenceType is "park" or "parks" && entry.AreaM2 > 0
                    ? Math.Log10(entry.AreaM2 / 1000.0 + 2.0)
                    : 1.0;
                return (Place: entry.Info, RouteDist: routeDist, Composite: routeDist / areaFactor);
            })
            .Where(x => x.RouteDist <= routeDistMax)
            .OrderBy(x => x.Composite)
            .ThenBy(x => x.Place.PlaceId)
            .ToList();

        var selected = new List<WaypointInfo>();
        foreach (var (place, _, _) in scored)
        {
            if (selected.Count >= maxCount) break;
            bool tooClose = selected.Any(s =>
                HaversineDistance(s.Latitude, s.Longitude, place.Latitude, place.Longitude) < 100);
            if (!tooClose)
                selected.Add(place);
        }

        // attach nearby unselected candidates as alternatives
        var selectedIds = selected.Select(s => s.PlaceId).ToHashSet();
        foreach (var s in selected)
        {
            s.Alternatives = scored
                .Where(x => !selectedIds.Contains(x.Place.PlaceId)
                         && HaversineDistance(s.Latitude, s.Longitude, x.Place.Latitude, x.Place.Longitude) <= AlternativesRadiusM)
                .Take(MaxAlternatives)
                .Select(x => x.Place)
                .ToList();
        }

        return selected;
    }

    private static int ClosestRouteIndex(double lat, double lng, List<(double Lat, double Lng)> route)
        => route.Select((p, i) => (i, d: HaversineDistance(lat, lng, p.Lat, p.Lng)))
                .MinBy(x => x.d).i;

    private static List<WaypointInfo> SortWaypointsByRouteOrder(
        List<WaypointInfo> waypoints, List<(double Lat, double Lng)> route)
    {
        return waypoints
            .Select(wp =>
            {
                var closestIdx = route
                    .Select((p, i) => new { i, d = HaversineDistance(wp.Latitude, wp.Longitude, p.Lat, p.Lng) })
                    .MinBy(x => x.d)!.i;
                return (wp, closestIdx);
            })
            .OrderBy(x => x.closestIdx)
            .Select(x => x.wp)
            .ToList();
    }

    private static List<(double Lat, double Lng)> GetSampledPoints(
        List<(double Lat, double Lng)> points, int count)
    {
        if (points.Count <= count) return points;
        var step = (double)(points.Count - 1) / (count - 1);
        return Enumerable.Range(0, count)
            .Select(i => points[(int)Math.Round(i * step)])
            .ToList();
    }

    private static double MinRouteDistance(double lat, double lng, List<(double Lat, double Lng)> route)
        => route.Min(p => HaversineDistance(lat, lng, p.Lat, p.Lng));

    private static double MinRouteDistanceBounded(
        double lat, double lng,
        (double NeLat, double NeLng, double SwLat, double SwLng) vp,
        List<(double Lat, double Lng)> route)
    {
        var centroidDist = MinRouteDistance(lat, lng, route);
        if (vp == default) return centroidDist;

        return Math.Min(centroidDist, new[]
        {
            MinRouteDistance(vp.NeLat, vp.NeLng, route),
            MinRouteDistance(vp.SwLat, vp.SwLng, route),
            MinRouteDistance(vp.NeLat, vp.SwLng, route),
            MinRouteDistance(vp.SwLat, vp.NeLng, route),
        }.Min());
    }

    private static double HaversineDistance(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371e3;
        var phi1 = lat1 * Math.PI / 180;
        var phi2 = lat2 * Math.PI / 180;
        var dPhi = (lat2 - lat1) * Math.PI / 180;
        var dLambda = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
              + Math.Cos(phi1) * Math.Cos(phi2)
              * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
