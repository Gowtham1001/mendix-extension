using System.ComponentModel.Composition;
using System.Net;
using Mendix.StudioPro.ExtensionsAPI.Services;
using Mendix.StudioPro.ExtensionsAPI.UI.WebServer;

namespace MendixVibeCoder;

[Export(typeof(WebServerExtension))]
public class VibeCoderWebServer : WebServerExtension
{
    private readonly IExtensionFileService _extensionFileService;

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        [".html"] = "text/html",
        [".js"] = "application/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon"
    };

    [ImportingConstructor]
    public VibeCoderWebServer(IExtensionFileService extensionFileService)
    {
        _extensionFileService = extensionFileService;
    }

    public override void InitializeWebServer(IWebServer webServer)
    {
        webServer.AddRoute("index", ServeIndex);
        webServer.AddRoute("app.js", ServeAppJs);
        webServer.AddRoute("styles.css", ServeStylesCss);
    }

    private async Task ServeIndex(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        await ServeFile(response, "index.html", ct);
    }

    private async Task ServeAppJs(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        await ServeFile(response, "app.js", ct);
    }

    private async Task ServeStylesCss(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        await ServeFile(response, "styles.css", ct);
    }

    private async Task ServeFile(HttpListenerResponse response, string fileName, CancellationToken ct)
    {
        try
        {
            var filePath = _extensionFileService.ResolvePath("wwwroot", fileName);

            if (!File.Exists(filePath))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            response.ContentType = MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
            response.StatusCode = (int)HttpStatusCode.OK;
            response.AddHeader("Access-Control-Allow-Origin", "*");

            var content = await File.ReadAllBytesAsync(filePath, ct);
            response.ContentLength64 = content.Length;
            await response.OutputStream.WriteAsync(content, ct);
            response.Close();
        }
        catch (Exception)
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            response.Close();
        }
    }
}
