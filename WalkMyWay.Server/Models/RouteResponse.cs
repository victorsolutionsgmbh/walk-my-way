namespace WalkMyWay.Server.Models;

public class RouteResponse
{
    public string GoogleMapsUrl { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public List<WaypointInfo> Waypoints { get; set; } = [];
}

public class WaypointInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string PlaceId { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}
