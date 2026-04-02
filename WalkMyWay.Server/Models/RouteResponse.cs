namespace WalkMyWay.Server.Models;

public class RouteResponse
{
    public string GoogleMapsUrl { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public List<WaypointInfo> Waypoints { get; set; } = [];
    /// <summary>true when at least one waypoint requires walking back along the route</summary>
    public bool HasBacktracks { get; set; }
}

public class WaypointInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string PlaceId { get; set; } = string.Empty;
    public double Rating { get; set; }
    public int UserRatingsTotal { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>null = no opening_hours data available; true/false = currently open/closed</summary>
    public bool? IsOpen { get; set; }
    public List<WaypointInfo> Alternatives { get; set; } = [];
    /// <summary>true when this stop lies slightly before the previous stop on the route (backtrack required)</summary>
    public bool BacktrackRequired { get; set; }
}
