namespace Ferry;

// The one place that knows where the Ferry server lives. Set once at startup
// from FERRY_PORT so that models, bindings and the client all agree; the old
// code hardcoded 127.0.0.1:8787 in FerryMessage.DownloadUrl, which silently
// pointed thumbnails at the live server while the rest of the app ran against
// a sandbox.
internal static class FerryEndpoint
{
    public static string BaseAddress { get; private set; } = "http://127.0.0.1:8787";

    public static int Port
    {
        get
        {
            if (Uri.TryCreate(BaseAddress, UriKind.Absolute, out var uri))
            {
                return uri.Port;
            }
            return 8787;
        }
    }

    public static void ResolveFromEnvironment()
    {
        var port = Environment.GetEnvironmentVariable("FERRY_PORT");
        if (int.TryParse(port, out var p) && p is > 0 and < 65536)
            BaseAddress = $"http://127.0.0.1:{p}";
    }
}
