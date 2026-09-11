using Ferry.Server;
using Xunit;

namespace Ferry.Server.Tests;

public sealed class ServerConfigurationTests
{
    [Fact]
    public void RequestBodyLimits_PreserveJsonAndUploadContract()
    {
        Assert.Equal(1_000_000, FerryServerInstance.JsonRequestBodyLimit);
        Assert.Equal(1024L * 1024L * 1024L, FerryServerInstance.UploadRequestBodyLimit);
    }

    [Fact]
    public void WebSocketHeartbeat_UsesProtocolPingAndSixtySecondTimeout()
    {
        var options = FerryServerInstance.CreateWebSocketOptions();

        Assert.Equal(TimeSpan.FromSeconds(25), options.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), options.KeepAliveTimeout);
    }
}
