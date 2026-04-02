namespace WalkMyWay.Server.Services;

public interface IGoogleApiLogger
{
    void LogRequest(string url);
    void LogResponse(string url, string responseBody, long elapsedMs);
    void LogError(string url, Exception ex);
}
