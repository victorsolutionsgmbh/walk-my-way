using WalkMyWay.Server.Models;

namespace WalkMyWay.Server.Services;

public class RouteCalculationService
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
            var seenPlaceIds = new HashSet<string>();
            var prefs = request.Preferences;
            var segmentSize = (double)routePoints.Count / prefs.Count;

            for (int i = 0; i < prefs.Count; i++)
            {
                var segStart = (int)Math.Round(i * segmentSize);
                var segEnd = (int)Math.Round((i + 1) * segmentSize);
                var segment = routePoints
                    .Skip(segStart)
                    .Take(Math.Max(1, segEnd - segStart))
                    .ToList();

                var places = await FindPlacesAlongRouteAsync(segment, prefs[i].Type, prefs[i].Count, prefs[i].OpenNow);
                foreach (var place in SortWaypointsByRouteOrder(places, segment))
                {
                    if (seenPlaceIds.Add(place.PlaceId))
                        waypoints.Add(place);
                }
            }
        }
        else
        {
            var seenPlaceIds = new HashSet<string>();
            foreach (var preference in request.Preferences)
            {
                var places = await FindPlacesAlongRouteAsync(routePoints, preference.Type, preference.Count, preference.OpenNow);
                foreach (var place in places)
                {
                    if (seenPlaceIds.Add(place.PlaceId))
                        waypoints.Add(place);
                }
            }
            waypoints = SortWaypointsByRouteOrder(waypoints, routePoints);
        }

        waypoints = waypoints.Take(9).ToList();

        return new RouteResponse
        {
            GoogleMapsUrl = _mapProvider.BuildNavigationUrl(
                request.CurrentLatitude, request.CurrentLongitude,
                request.DestinationAddress, waypoints),
            DestinationAddress = routeData.EndAddress,
            Waypoints = waypoints
        };
    }

    private async Task<List<WaypointInfo>> FindPlacesAlongRouteAsync(
        List<(double Lat, double Lng)> routePoints, string preferenceType, int maxCount, bool openNow)
    {
        var found = new Dictionary<string, (WaypointInfo Info, (double NeLat, double NeLng, double SwLat, double SwLng) Viewport)>();
        var samplePoints = GetSampledPoints(routePoints, 5);

        foreach (var (lat, lng) in samplePoints)
        {
            var candidates = await _mapProvider.SearchPlacesNearbyAsync(lat, lng, 800, preferenceType, openNow);
            foreach (var c in candidates)
            {
                if (!found.ContainsKey(c.PlaceId))
                {
                    found[c.PlaceId] = (new WaypointInfo
                    {
                        PlaceId = c.PlaceId,
                        Name = c.Name,
                        Type = preferenceType,
                        Address = c.Address,
                        Rating = c.Rating,
                        UserRatingsTotal = c.UserRatingsTotal,
                        Latitude = c.Latitude,
                        Longitude = c.Longitude
                    }, c.Viewport);
                }
            }
        }

        var scored = found.Values
            .Select(entry =>
            {
                var routeDist = MinRouteDistanceBounded(
                    entry.Info.Latitude, entry.Info.Longitude, entry.Viewport, routePoints);
                var rating = entry.Info.Rating > 0 ? entry.Info.Rating : 3.0;
                var popularity = Math.Log10(entry.Info.UserRatingsTotal + 10);
                popularity *= popularity;
                var composite = routeDist / (rating * rating * rating * popularity);
                return (Place: entry.Info, RouteDist: routeDist, Composite: composite);
            })
            .Where(x => x.RouteDist <= 250)
            .OrderBy(x => x.Composite)
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
        return selected;
    }

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
