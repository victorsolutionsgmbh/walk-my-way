using Npgsql;
using WalkMyWay.Server;
using WalkMyWay.Server.Services;

namespace WalkMyWay.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services.AddControllers();
        builder.Services.AddSingleton<GoogleApiLogger>();

        var connString = builder.Configuration.GetConnectionString("Osm")
            ?? throw new InvalidOperationException("ConnectionStrings:Osm is not configured.");

        // Disable PostgreSQL parallel query workers for all connections from this app.
        // Our queries use LIMIT + spatial/GIN indexes and gain nothing from parallelism.
        // Without this, the planner (influenced by GIN index statistics) may spawn workers
        // that exhaust /dev/shm in Docker containers (default 64 MB), causing error 53100.
        var csb = new NpgsqlConnectionStringBuilder(connString)
        {
            Options = "-c max_parallel_workers_per_gather=0"
        };
        builder.Services.AddSingleton(NpgsqlDataSource.Create(csb.ToString()));

        builder.Services.AddHttpClient<IMapProvider, PostgresOsmProvider>();

        builder.Services.AddScoped<RouteCalculationService>();

        var app = builder.Build();

        var initLogger = app.Services.GetRequiredService<ILogger<Program>>();
        await OsmDatabaseInitializer.InitializeAsync(connString, initLogger);

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.MapFallbackToFile("/index.html");

        app.Run();
    }
}
