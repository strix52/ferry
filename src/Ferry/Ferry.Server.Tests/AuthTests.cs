using System.Net;
using Ferry.Server.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ferry.Server.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly string _tempDir;

    public AuthTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ferry-auth-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    public void IsLoopback_IdentifiesLoopbackAddresses(string ipStr, bool expected)
    {
        var ip = IPAddress.Parse(ipStr);
        Assert.Equal(expected, FerryAuth.IsLoopback(ip));
    }

    [Fact]
    public void IsAuthorized_NonLoopback_WithoutToken_ReturnsFalse_401Path()
    {
        var auth = new FerryAuth(_tempDir);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.150");

        // No token provided from external IP -> must be rejected (401 path)
        var authorized = auth.IsAuthorized(context);
        Assert.False(authorized);
    }

    [Fact]
    public void IsAuthorized_NonLoopback_WithInvalidToken_ReturnsFalse()
    {
        var auth = new FerryAuth(_tempDir);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.150");
        context.Request.Headers["x-ferry-token"] = "invalid-token-value-here";

        var authorized = auth.IsAuthorized(context);
        Assert.False(authorized);
    }

    [Fact]
    public void IsAuthorized_NonLoopback_WithValidHeaderToken_ReturnsTrue()
    {
        var auth = new FerryAuth(_tempDir);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.150");
        context.Request.Headers["x-ferry-token"] = auth.Config.Token;

        var authorized = auth.IsAuthorized(context);
        Assert.True(authorized);
    }

    [Fact]
    public void IsAuthorized_NonLoopback_WithValidQueryToken_ReturnsTrue()
    {
        var auth = new FerryAuth(_tempDir);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.150");
        context.Request.QueryString = new QueryString($"?token={auth.Config.Token}");

        var authorized = auth.IsAuthorized(context);
        Assert.True(authorized);
    }

    [Fact]
    public void PublicInfo_UnauthorizedCaller_HasNullPrimaryAndPairedFalse()
    {
        var auth = new FerryAuth(_tempDir);
        var info = auth.GetPublicInfo(8787, includeToken: false);

        Assert.Equal(8787, info.Port);
        Assert.False(info.Auth.Paired);
        if (info.Primary != null)
        {
            Assert.DoesNotContain("?token=", info.Primary);
        }
    }

    [Fact]
    public void PublicInfo_AuthorizedCaller_HasPrimaryWithTokenAndPairedTrue()
    {
        var auth = new FerryAuth(_tempDir);
        var info = auth.GetPublicInfo(8787, includeToken: true);

        Assert.Equal(8787, info.Port);
        Assert.True(info.Auth.Paired);
        if (info.Primary != null)
        {
            Assert.Contains("?token=", info.Primary);
        }
    }
}
