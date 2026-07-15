using System.ComponentModel.Composition;
using System.Net;
using Mendix.StudioPro.ExtensionsAPI.UI.WebServer;

namespace MendixVibeCoder;

[Export(typeof(WebServerExtension))]
public class VibeCoderWebServer : WebServerExtension
{
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

    public override void InitializeWebServer(IWebServer webServer)
    {
        webServer.AddRoute("index", ServeIndex);
        webServer.AddRoute("app.js", ServeAppJs);
        webServer.AddRoute("styles.css", ServeStylesCss);
    }

    private static async Task ServeIndex(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        await ServeFile(request, response, "index.html", ct);
    }

    private static async Task ServeAppJs(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        await ServeFile(request, response, "app.js", ct);
    }

    private static async Task ServeStylesCss(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        await ServeFile(request, response, "styles.css", ct);
    }

    private static async Task ServeFile(HttpListenerRequest request, HttpListenerResponse response, string fileName, CancellationToken ct)
    {
        try
        {
            var extensionDir = AppDomain.CurrentDomain.BaseDirectory;
            var filePath = Path.Combine(extensionDir, fileName);

            if (!File.Exists(filePath))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            response.ContentType = MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";

            var content = await File.ReadAllBytesAsync(filePath, ct);
            response.ContentLength64 = content.Length;
            await response.OutputStream.WriteAsync(content, ct);
        }
        catch (Exception)
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }
}
