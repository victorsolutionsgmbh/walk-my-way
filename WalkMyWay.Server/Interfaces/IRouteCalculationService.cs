using WalkMyWay.Server.Models;

namespace WalkMyWay.Server.Services;

public interface IRouteCalculationService
{
    Task<RouteResponse> GetWalkingRouteAsync(RouteRequest request);
}
