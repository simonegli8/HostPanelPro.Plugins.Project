using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HostPanelPro.Plugins;

public class Download
{
    public static CancellationTokenSource Cancel => PluginManager.Cancel;
    public static HttpClientHandler Proxy => PluginManager.Proxy;

    /*public HttpClientHandler ProxyHandler()
    {
        if (Installer.Current.Settings.Installer.Proxy != null && !string.IsNullOrEmpty(Installer.Current.Settings.Installer.Proxy.Address))
        {
            var settings = Installer.Current.Settings.Installer.Proxy;
            var proxy = new WebProxy(settings.Address, false);  // proxy address + port
            if (!string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(settings.Password))
            {
                proxy.Credentials = new NetworkCredential(settings.Username, settings.Password); // if proxy needs auth
            }

            // Attach proxy to handler
            return new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true
            };
        }
        return null;
    }*/

    public static async Task DownloadFileAndUnzipAsync(string url, string destinationFile, string destinationPath = null, Func<string, bool> filter = null,
        Func<long, long, Task> progress = null)
    {
        if (string.IsNullOrEmpty(destinationPath)) destinationPath = Path.GetDirectoryName(destinationFile);
        if (!Directory.Exists(destinationPath)) Directory.CreateDirectory(destinationPath);

        if (url.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var singleFile = url.EndsWith(".dll.zip", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".dll.7z", StringComparison.OrdinalIgnoreCase);
            using (var stream = new SeekableDownloadStream(url, destinationFile + ".tmp", true, singleFile ? progress : null))
            {
                await Zip.UnzipFile(destinationFile, destinationPath, filter, stream, singleFile ? null : progress);
            }
        }
        else throw new NotSupportedException("Url must be a 7z or zip archive");
    }
    public static async Task<long> GetFileSizeAsync(string url)
    {
        var handler = Proxy;
        using var client = handler != null ? new HttpClient(handler) : new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await client.SendAsync(request, Cancel.Token);
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength ?? -1;
    }

    public static async Task<bool> FileExistsAsync(string url)
    {
        var handler = Proxy;
        using var client = handler != null ? new HttpClient(handler) : new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await client.SendAsync(request, Cancel.Token);
        return response.IsSuccessStatusCode;
    }
    public static async Task<long> DownloadFileChunkAsync(string url, long offset, long length, byte[] buffer)
    {
        if (length <= 0) return 0;
        var handler = Proxy;
        using var client = handler != null ? new HttpClient(handler) : new HttpClient();
        client.DefaultRequestHeaders.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, Cancel.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent) throw new InvalidDataException("Request did not return a PartialContent status code.");
        using var stream = await response.Content.ReadAsStreamAsync();
        var size = response.Content.Headers.ContentLength ?? 0;
        int totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, totalRead, (int)size - totalRead, Cancel.Token)) > 0)
        {
            totalRead += read;
        }
        return size;
    }
    public static async Task<long> DownloadFileChunkAsync(string url, long offset, long length, Stream stream)
    {
        if (length <= 0) return 0;
        var handler = Proxy;
        using var client = handler != null ? new HttpClient(handler) : new HttpClient();
        client.DefaultRequestHeaders.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, Cancel.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent) throw new InvalidDataException("Request did not return a PartialContent status code.");
        await response.Content.CopyToAsync(stream);
        return response.Content.Headers.ContentLength ?? 0;
    }

    public static async Task DownloadAndUnzip(string url, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        await DownloadFileAndUnzipAsync(url, $"{Path.GetTempFileName()}.{Path.GetExtension(url)}", destination);
    }
}
