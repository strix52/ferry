using Ferry.Server;

var port = 8787;
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var envPort))
{
    port = envPort;
}

var dataDir = Environment.GetEnvironmentVariable("FERRY_DATA_DIR") ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
var publicDir = Path.Combine(Directory.GetCurrentDirectory(), "public");

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPort))
    {
        port = parsedPort;
        i++;
    }
    else if (args[i] == "--data-dir" && i + 1 < args.Length)
    {
        dataDir = args[i + 1];
        i++;
    }
    else if (args[i] == "--public-dir" && i + 1 < args.Length)
    {
        publicDir = args[i + 1];
        i++;
    }
}

var config = new FerryServerConfig
{
    Port = port,
    DataDir = dataDir,
    PublicDir = publicDir,
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var instance = await FerryServerInstance.StartAsync(config, cts.Token);
Console.WriteLine($"Ferry.ServerHost running on port {port}");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Graceful shutdown
}

await instance.StopAsync();
