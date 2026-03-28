using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WalkMyWay.Server.Models;

namespace WalkMyWay.Server.Services;

public class GoogleMapsService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly GoogleApiLogger _apiLogger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly SemaphoreSlim _googleApiRateLimiter = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;
    private const int MinRequestIntervalMs = 150;

    public GoogleMapsService(HttpClient httpClient, IConfiguration configuration, GoogleApiLogger apiLogger)
    {
        _httpClient = httpClient;
        _apiLogger = apiLogger;
        _apiKey = configuration["GoogleMaps:ApiKey"]
            ?? throw new InvalidOperationException("GoogleMaps:ApiKey is not configured.");
    }

    private async Task<string> ThrottledGetStringAsync(string url)
    {
        await _googleApiRateLimiter.WaitAsync();
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
            _googleApiRateLimiter.Release();
        }
    }

    public async Task<string> ReverseGeocodeAsync(double lat, double lng)
    {
        var url = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={lat.ToString(CultureInfo.InvariantCulture)},{lng.ToString(CultureInfo.InvariantCulture)}&key={_apiKey}";
        var json = await ThrottledGetStringAsync(url);
        var response = JsonSerializer.Deserialize<GeocodingApiResponse>(json, JsonOptions);
        return response?.Results?.FirstOrDefault()?.FormattedAddress ?? $"{lat:F6}, {lng:F6}";
    }

    public async Task<RouteResponse> GetWalkingRouteAsync(RouteRequest request)
    {
        var directionsUrl = BuildDirectionsUrl(request.CurrentLatitude, request.CurrentLongitude, request.DestinationAddress);
        var directionsJson = await ThrottledGetStringAsync(directionsUrl);
        var directions = JsonSerializer.Deserialize<DirectionsApiResponse>(directionsJson, JsonOptions);

        if (directions?.Status != "OK" || directions.Routes == null || directions.Routes.Length == 0)
            throw new InvalidOperationException("No walking route found to the specified destination.");

        var route = directions.Routes[0];
        var routePoints = DecodePolyline(route.OverviewPolyline.Points);
        var destinationAddress = route.Legs?.FirstOrDefault()?.EndAddress ?? request.DestinationAddress;

        var waypoints = new List<WaypointInfo>();
        foreach (var preference in request.Preferences)
        {
            var maxCount = preference.Count <= 0 ? 5 : preference.Count;
            var places = await FindPlacesAlongRouteAsync(routePoints, preference.Type, maxCount);
            waypoints.AddRange(places);
        }

        waypoints = SortWaypointsByRouteOrder(waypoints, routePoints);
        waypoints = waypoints.Take(9).ToList();

        return new RouteResponse
        {
            GoogleMapsUrl = BuildGoogleMapsUrl(request.CurrentLatitude, request.CurrentLongitude, request.DestinationAddress, waypoints),
            DestinationAddress = destinationAddress,
            Waypoints = waypoints
        };
    }

    private async Task<List<WaypointInfo>> FindPlacesAlongRouteAsync(
        List<(double lat, double lng)> routePoints, string preferenceType, int maxCount)
    {
        var googleType = MapToGoogleType(preferenceType);
        var found = new Dictionary<string, WaypointInfo>();
        var samplePoints = GetSampledPoints(routePoints, 3);

        foreach (var (lat, lng) in samplePoints)
        {
            var url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json?location={lat.ToString(CultureInfo.InvariantCulture)},{lng.ToString(CultureInfo.InvariantCulture)}&radius=800&type={googleType}&key={_apiKey}";
            var json = await ThrottledGetStringAsync(url);
            var response = JsonSerializer.Deserialize<PlacesApiResponse>(json, JsonOptions);

            if (response?.Status != "OK") continue;

            foreach (var place in response.Results.Take(5))
            {
                if (!found.ContainsKey(place.PlaceId))
                {
                    found[place.PlaceId] = new WaypointInfo
                    {
                        Name = place.Name,
                        Type = preferenceType,
                        Address = place.Vicinity,
                        PlaceId = place.PlaceId,
                        Latitude = place.Geometry.Location.Lat,
                        Longitude = place.Geometry.Location.Lng
                    };
                }
            }
        }

        // Score by minimum distance to route, filter to places reasonably close to the route.
        // 500m allows for parks whose registered coordinate is at the centroid/entrance.
        var scored = found.Values
            .Select(p => (Place: p, RouteDist: MinRouteDistance(p.Latitude, p.Longitude, routePoints)))
            .Where(x => x.RouteDist <= 500)
            .OrderBy(x => x.RouteDist)
            .ToList();

        // Greedy cluster deduplication within this type:
        // prefer the place closest to the route; skip any that is within 250m of an already-selected place.
        // This prevents multiple parks/cafes that are clustered together from all being selected.
        var selected = new List<WaypointInfo>();
        foreach (var (place, _) in scored)
        {
            if (selected.Count >= maxCount) break;
            bool tooClose = selected.Any(s =>
                HaversineDistance(s.Latitude, s.Longitude, place.Latitude, place.Longitude) < 250);
            if (!tooClose)
                selected.Add(place);
        }
        return selected;
    }

    public async Task<List<PlaceSuggestion>> GetPlaceAutocompleteSuggestionsAsync(
        string input, double? lat, double? lng)
    {
        var encodedInput = Uri.EscapeDataString(input);
        var url = $"https://maps.googleapis.com/maps/api/place/autocomplete/json?input={encodedInput}&language=de&key={_apiKey}";
        if (lat.HasValue && lng.HasValue)
            url += $"&location={lat.Value.ToString(CultureInfo.InvariantCulture)},{lng.Value.ToString(CultureInfo.InvariantCulture)}&radius=50000";

        var json = await ThrottledGetStringAsync(url);
        var response = JsonSerializer.Deserialize<AutocompleteApiResponse>(json, JsonOptions);

        if (response?.Status != "OK" || response.Predictions == null)
            return [];

        return response.Predictions
            .Select(p => new PlaceSuggestion { Description = p.Description, PlaceId = p.PlaceId })
            .ToList();
    }

    private string BuildDirectionsUrl(double lat, double lng, string destination)
    {
        return $"https://maps.googleapis.com/maps/api/directions/json?origin={lat.ToString(CultureInfo.InvariantCulture)},{lng.ToString(CultureInfo.InvariantCulture)}&destination={Uri.EscapeDataString(destination)}&mode=walking&key={_apiKey}";
    }

    private static string BuildGoogleMapsUrl(
        double originLat, double originLng, string destination, List<WaypointInfo> waypoints)
    {
        var sb = new StringBuilder();
        sb.Append("https://www.google.com/maps/dir/?api=1");
        sb.Append($"&origin={originLat.ToString(CultureInfo.InvariantCulture)},{originLng.ToString(CultureInfo.InvariantCulture)}");
        sb.Append($"&destination={Uri.EscapeDataString(destination)}");

        if (waypoints.Count > 0)
        {
            var wps = string.Join("|", waypoints.Select(w =>
                string.IsNullOrEmpty(w.Name)
                    ? $"{w.Latitude.ToString(CultureInfo.InvariantCulture)},{w.Longitude.ToString(CultureInfo.InvariantCulture)}"
                    : w.Name));
            sb.Append($"&waypoints={Uri.EscapeDataString(wps)}");
        }

        sb.Append("&travelmode=walking");
        return sb.ToString();
    }

    private static double MinRouteDistance(double lat, double lng, List<(double lat, double lng)> route)
        => route.Min(p => HaversineDistance(lat, lng, p.lat, p.lng));

    private static List<(double lat, double lng)> GetSampledPoints(
        List<(double lat, double lng)> points, int count)
    {
        if (points.Count <= count) return points;
        var step = (double)(points.Count - 1) / (count - 1);
        return Enumerable.Range(0, count)
            .Select(i => points[(int)Math.Round(i * step)])
            .ToList();
    }

    private static List<WaypointInfo> SortWaypointsByRouteOrder(
        List<WaypointInfo> waypoints, List<(double lat, double lng)> route)
    {
        return waypoints
            .Select(wp =>
            {
                var closestIdx = route
                    .Select((p, i) => new { i, d = HaversineDistance(wp.Latitude, wp.Longitude, p.lat, p.lng) })
                    .MinBy(x => x.d)!.i;
                return (wp, closestIdx);
            })
            .OrderBy(x => x.closestIdx)
            .Select(x => x.wp)
            .ToList();
    }

    private static string MapToGoogleType(string preference) => preference.ToLower() switch
    {
        "cafe" or "coffee" => "cafe",
        "park" or "parks" => "park",
        "restaurant" => "restaurant",
        "bakery" => "bakery",
        "supermarket" or "grocery" => "supermarket",
        "pharmacy" or "drugstore" => "pharmacy",
        "bar" or "pub" => "bar",
        "museum" => "museum",
        "gym" or "fitness" => "gym",
        "library" => "library",
        "bank" or "atm" => "bank",
        "convenience_store" or "convenience" => "convenience_store",
        _ => preference.ToLower().Replace(" ", "_")
    };

    private static List<(double lat, double lng)> DecodePolyline(string encoded)
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

    private class GeocodingApiResponse
    {
        public string Status { get; set; } = string.Empty;
        public GeocodingResult[]? Results { get; set; }
    }

    private class GeocodingResult
    {
        [JsonPropertyName("formatted_address")]
        public string FormattedAddress { get; set; } = string.Empty;
    }

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

    private class PlacesApiResponse
    {
        public string Status { get; set; } = string.Empty;
        public PlaceResult[] Results { get; set; } = [];
    }

    private class PlaceResult
    {
        [JsonPropertyName("place_id")]
        public string PlaceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Vicinity { get; set; } = string.Empty;
        public PlaceGeometry Geometry { get; set; } = new();
    }

    private class PlaceGeometry
    {
        public PlaceLocation Location { get; set; } = new();
    }

    private class PlaceLocation
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
    }

    private class AutocompleteApiResponse
    {
        public string Status { get; set; } = string.Empty;
        public AutocompletePrediction[]? Predictions { get; set; }
    }

    private class AutocompletePrediction
    {
        public string Description { get; set; } = string.Empty;
        [JsonPropertyName("place_id")]
        public string PlaceId { get; set; } = string.Empty;
    }
}

public class PlaceSuggestion
{
    public string Description { get; set; } = string.Empty;
    public string PlaceId { get; set; } = string.Empty;
}
