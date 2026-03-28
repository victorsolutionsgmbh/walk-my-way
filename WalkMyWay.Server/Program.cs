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
        builder.Services.AddSingleton(NpgsqlDataSource.Create(connString));

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
