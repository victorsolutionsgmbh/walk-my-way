namespace WalkMyWay.Server;

public static class AppConstants
{
    /// <summary>Maximum number of waypoint stops a user can request in a single route.</summary>
    public const int MaxStops = 40;

    /// <summary>
    /// Max distance (metres) between a candidate stop and the current route position
    /// at which a backwards stop is still allowed and flagged as a backtrack.
    /// </summary>
    public const int BacktrackThresholdM = 100;
}
