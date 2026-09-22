using System.Net;
using System.Text;
using Ferry.Cli;
using Xunit;

namespace Ferry.Cli.Tests;

public sealed class FerryCliTests
{
    [Fact]
    public async Task Status_ExcludesLaptopIdentity()
    {
        using var fixture = new IdentityFixture();
        var handler = new StubHandler(request => Json("""{"presence":[{"id":"laptop-id","name":"Laptop"},{"id":"phone-id","name":"Phone"}]}"""));
        using var output = new StringWriter();

        var exit = await FerryCli.RunAsync(["status"], output, TextWriter.Null, handler, fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("\"phoneConnected\":true", output.ToString());
        Assert.DoesNotContain("Laptop", output.ToString());
        Assert.Contains("Phone", output.ToString());
    }

    [Fact]
    public async Task SendText_StopsWhenPhoneIsNotConnected()
    {
        using var fixture = new IdentityFixture();
        var requests = 0;
        var handler = new StubHandler(request =>
        {
            requests++;
            return Json("""{"presence":[{"id":"laptop-id","name":"Laptop"}]}""");
        });
        using var error = new StringWriter();

        var exit = await FerryCli.RunAsync(["send-text", "hello"], TextWriter.Null, error, handler, fixture.Root);

        Assert.Equal(4, exit);
        Assert.Equal(1, requests);
        Assert.Contains("phone_not_connected", error.ToString());
    }

    [Fact]
    public async Task SendText_PostsAfterPresenceGate()
    {
        using var fixture = new IdentityFixture();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/presence" => Json("""{"presence":[{"id":"phone-id","name":"Phone"}]}"""),
            "/api/messages" => Json("""{"ok":true,"id":42}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var output = new StringWriter();

        var exit = await FerryCli.RunAsync(["send-text", "hello", "phone"], output, TextWriter.Null, handler, fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("\"id\":42", output.ToString());
        Assert.Equal(HttpMethod.Post, handler.Requests.Last().Method);
        Assert.Contains("hello phone", handler.Bodies.Last());
    }

    [Fact]
    public async Task Recent_TruncatesLongTextAndReadReturnsItWhole()
    {
        using var fixture = new IdentityFixture();
        var text = new string('x', 240);
        var handler = new StubHandler(request => Json($$"""[{"id":7,"kind":"text","senderId":"phone","senderName":"Phone","text":"{{text}}","filename":null,"size":null,"deleted":false,"createdAt":1}]"""));
        using var recentOutput = new StringWriter();
        using var readOutput = new StringWriter();

        Assert.Equal(0, await FerryCli.RunAsync(["recent"], recentOutput, TextWriter.Null, handler, fixture.Root));
        Assert.Equal(0, await FerryCli.RunAsync(["read", "7"], readOutput, TextWriter.Null, handler, fixture.Root));

        Assert.Contains("\"truncated\":true", recentOutput.ToString());
        Assert.DoesNotContain(text, recentOutput.ToString());
        Assert.Contains(text, readOutput.ToString());
    }

    [Fact]
    public void ResolveDestination_AddsCollisionSuffix()
    {
        var root = Path.Combine(Path.GetTempPath(), "ferry-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "photo.jpg"), "one");
            var destination = FerryAgentClient.ResolveDestination(root + Path.DirectorySeparatorChar, "photo.jpg");
            Assert.Equal(Path.Combine(root, "photo (1).jpg"), destination);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return response(request);
        }
    }

    private sealed class IdentityFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ferry-cli-identity-" + Guid.NewGuid().ToString("N"));

        public IdentityFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "Ferry"));
            File.WriteAllText(Path.Combine(Root, "Ferry", "device.json"), """{"Id":"laptop-id","Name":"Laptop"}""");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
