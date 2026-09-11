using Ferry.Server.Auth;
using Ferry.Server.Data;
using Ferry.Server.Endpoints;
using Ferry.Server.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.Logging;

namespace Ferry.Server;

public sealed class FerryServerConfig
{
    public int Port { get; init; } = 8787;
    public string DataDir { get; init; } = "";
    public string PublicDir { get; init; } = "";
}

public sealed class FerryServerInstance : IAsyncDisposable
{
    internal const long JsonRequestBodyLimit = 1_000_000;
    internal const long UploadRequestBodyLimit = 1024L * 1024L * 1024L;

    private readonly WebApplication _app;
    private readonly FerryDatabase _db;
    private readonly FerryAuth _auth;
    private readonly FerryWebSocketHub _hub;

    public int Port { get; }
    public FerryDatabase Database => _db;
    public FerryAuth Auth => _auth;
    public FerryWebSocketHub Hub => _hub;

    public FerryServerInstance(WebApplication app, FerryDatabase db, FerryAuth auth, FerryWebSocketHub hub, int port)
    {
        _app = app;
        _db = db;
        _auth = auth;
        _hub = hub;
        Port = port;
    }

    public static async Task<FerryServerInstance> StartAsync(FerryServerConfig config, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(config.Port);
            options.Limits.MaxRequestBodySize = JsonRequestBodyLimit;
        });

        builder.Logging.ClearProviders();

        var db = new FerryDatabase(config.DataDir);
        var auth = new FerryAuth(config.DataDir);
        var hub = new FerryWebSocketHub();
        var endpoints = new FerryEndpoints(db, auth, hub, config);

        var app = builder.Build();

        app.UseWebSockets(CreateWebSocketOptions());

        app.Run(endpoints.HandleRequestAsync);

        await app.StartAsync(ct);
        return new FerryServerInstance(app, db, auth, hub, config.Port);
    }

    internal static WebSocketOptions CreateWebSocketOptions() => new()
    {
        KeepAliveInterval = TimeSpan.FromSeconds(25),
        KeepAliveTimeout = TimeSpan.FromSeconds(60),
    };

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _app.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _hub.DisposeAsync();
        _db.Dispose();
    }
}
