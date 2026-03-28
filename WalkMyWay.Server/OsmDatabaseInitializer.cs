using System.Diagnostics;
using Npgsql;

namespace WalkMyWay.Server;

public static class OsmDatabaseInitializer
{
    private const string OsmDownloadUrl = "https://download.geofabrik.de/europe/austria-latest.osm.pbf";
    private const string OsmFileName = "austria-latest.osm.pbf";
    private const string TempDir = "/tmp/osm-data";

    public static async Task InitializeAsync(string connectionString, ILogger logger)
    {
        logger.LogInformation("=== OSM Database Initialization Check ===");

        try
        {
            if (await IsDatabasePopulatedAsync(connectionString, logger))
            {
                logger.LogInformation("OSM database already populated. Skipping initialization.");
                return;
            }

            logger.LogInformation("OSM database is empty or tables are missing. Starting full import...");

            var pbfPath = await DownloadOsmDataAsync(logger);
            await CreateExtensionsAsync(connectionString, logger);
            await RunOsm2PgsqlAsync(connectionString, pbfPath, logger);

            logger.LogInformation("Verifying database after import...");
            if (!await IsDatabasePopulatedAsync(connectionString, logger))
            {
                logger.LogCritical("Database verification failed after osm2pgsql import. Tables still missing. Terminating container.");
                Environment.Exit(1);
            }

            logger.LogInformation("=== OSM Database initialized successfully ===");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error during OSM database initialization. Terminating container.");
            Environment.Exit(1);
        }
    }

    private static async Task<bool> IsDatabasePopulatedAsync(string connectionString, ILogger logger)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'planet_osm_roads')",
                conn);

            var result = await cmd.ExecuteScalarAsync();
            var exists = result is true;

            logger.LogInformation("Database check: planet_osm_roads table {Status}.", exists ? "EXISTS" : "NOT FOUND");
            return exists;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not connect to database or query tables. Assuming empty/uninitialized.");
            return false;
        }
    }

    private static async Task<string> DownloadOsmDataAsync(ILogger logger)
    {
        Directory.CreateDirectory(TempDir);
        var pbfPath = Path.Combine(TempDir, OsmFileName);

        if (File.Exists(pbfPath))
        {
            logger.LogInformation("OSM PBF file already exists at {Path}. Skipping download.", pbfPath);
            return pbfPath;
        }

        logger.LogInformation("Downloading OSM data from {Url}...", OsmDownloadUrl);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(3) };

        using var response = await httpClient.GetAsync(OsmDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes.HasValue)
            logger.LogInformation("File size: {SizeMB} MB", totalBytes.Value / 1024 / 1024);

        await using var fileStream = new FileStream(pbfPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        await using var contentStream = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int bytesRead;
        var lastLogTime = DateTime.UtcNow;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloadedBytes += bytesRead;

            if (DateTime.UtcNow - lastLogTime >= TimeSpan.FromSeconds(15))
            {
                if (totalBytes.HasValue)
                    logger.LogInformation("Download progress: {DownloadedMB} / {TotalMB} MB ({Percent:F1}%)",
                        downloadedBytes / 1024 / 1024, totalBytes.Value / 1024 / 1024,
                        (double)downloadedBytes / totalBytes.Value * 100);
                else
                    logger.LogInformation("Download progress: {DownloadedMB} MB downloaded", downloadedBytes / 1024 / 1024);

                lastLogTime = DateTime.UtcNow;
            }
        }

        logger.LogInformation("Download complete. Saved to {Path}.", pbfPath);
        return pbfPath;
    }

    private static async Task CreateExtensionsAsync(string connectionString, ILogger logger)
    {
        logger.LogInformation("Creating PostGIS and hstore extensions...");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS hstore;";
        await cmd.ExecuteNonQueryAsync();

        logger.LogInformation("Extensions created successfully.");
    }

    private static async Task RunOsm2PgsqlAsync(string connectionString, string pbfPath, ILogger logger)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        var args = string.Join(" ",
            "--create", "--slim", "--drop", "--hstore",
            $"--database {builder.Database}",
            $"--username {builder.Username}",
            $"--host {builder.Host}",
            $"--port {builder.Port ?? 5432}",
            "--number-processes 4",
            "--cache 2000",
            $"\"{pbfPath}\"");

        logger.LogInformation("Starting osm2pgsql import (this may take a while)...");
        logger.LogInformation("osm2pgsql args: {Args}", args);

        var psi = new ProcessStartInfo("osm2pgsql", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["PGPASSWORD"] = builder.Password ?? string.Empty;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start osm2pgsql process.");

        var outputTask = ForwardStreamAsync(process.StandardOutput, line => logger.LogInformation("[osm2pgsql] {Line}", line));
        var errorTask = ForwardStreamAsync(process.StandardError, line => logger.LogInformation("[osm2pgsql] {Line}", line));

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"osm2pgsql exited with non-zero exit code {process.ExitCode}.");

        logger.LogInformation("osm2pgsql import completed successfully.");
    }

    private static async Task ForwardStreamAsync(StreamReader reader, Action<string> log)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
            log(line);
    }
}
