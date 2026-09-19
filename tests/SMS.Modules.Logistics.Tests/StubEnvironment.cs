using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// A web host environment with a web root that does not exist — so the letterhead logo is simply
/// absent, which is the state a fresh deployment is in and the one every document must survive.
/// </summary>
internal sealed class StubEnvironment : IWebHostEnvironment
{
    public string        WebRootPath             { get; set; } = Path.Combine(Path.GetTempPath(), "no-such-webroot");
    public IFileProvider WebRootFileProvider     { get; set; } = new NullFileProvider();
    public string        ApplicationName         { get; set; } = "Tests";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string        ContentRootPath         { get; set; } = Path.GetTempPath();
    public string        EnvironmentName         { get; set; } = "Test";

    private sealed class NullFileProvider : IFileProvider
    {
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class NullChangeToken : IChangeToken
    {
        internal static readonly NullChangeToken Singleton = new();
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
