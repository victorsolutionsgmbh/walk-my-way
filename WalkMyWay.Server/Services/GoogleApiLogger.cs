namespace WalkMyWay.Server.Services;

public sealed class GoogleApiLogger : IGoogleApiLogger, IDisposable
{
    private readonly string _logDirectory;
    private readonly object _lock = new();

    public GoogleApiLogger(IConfiguration configuration)
    {
        _logDirectory = configuration["GoogleApiLog:Directory"] ?? "logs";
        Directory.CreateDirectory(_logDirectory);
    }

    public void LogRequest(string url)
    {
        var masked = MaskApiKey(url);
        Write($"REQUEST  {masked}");
    }

    public void LogResponse(string url, string responseBody, long elapsedMs)
    {
        var masked = MaskApiKey(url);
        var preview = responseBody.Length > AppConstants.ApiLogResponsePreviewLength
            ? responseBody[..AppConstants.ApiLogResponsePreviewLength] + "…[truncated]"
            : responseBody;
        Write($"RESPONSE {masked} ({elapsedMs} ms){Environment.NewLine}{preview}");
    }

    public void LogError(string url, Exception ex)
    {
        var masked = MaskApiKey(url);
        Write($"ERROR    {masked}{Environment.NewLine}{ex}");
    }

    private void Write(string message)
    {
        var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}{Environment.NewLine}";
#if DEBUG
        var filePath = Path.Combine(_logDirectory, $"google-api-{DateTime.UtcNow:yyyy-MM-dd}.log");
        lock (_lock)
        {
            File.AppendAllText(filePath, line);
        }
#endif
    }

    private static string MaskApiKey(string url)
    {
        var idx = url.IndexOf("key=", StringComparison.Ordinal);
        if (idx < 0) return url;
        var end = url.IndexOf('&', idx);
        var keyValue = end < 0 ? url[(idx + 4)..] : url[(idx + 4)..end];
        if (keyValue.Length <= 8) return url.Replace(keyValue, "***");
        var masked = keyValue[..4] + "***" + keyValue[^4..];
        return url.Replace(keyValue, masked);
    }

    public void Dispose() { }
}
