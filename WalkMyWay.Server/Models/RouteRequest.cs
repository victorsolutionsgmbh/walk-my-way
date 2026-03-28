namespace WalkMyWay.Server.Models;

public class RouteRequest
{
    public double CurrentLatitude { get; set; }
    public double CurrentLongitude { get; set; }
    public string DestinationAddress { get; set; } = string.Empty;
    public List<PreferenceItem> Preferences { get; set; } = [];
}

public class PreferenceItem
{
    public string Type { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool OpenNow { get; set; }
}
