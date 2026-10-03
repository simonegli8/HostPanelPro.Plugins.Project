using EstrellasDeEsperanza.AsyncLock;
#if !PackAsTool
using HostPanelPro.Web.Services;
using HostPanelPro.Providers.OS;
#endif
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace HostPanelPro.Plugins;


public struct PluginId
{
    public string Name;

    [JsonIgnore]
    public string EncodedName
    {
        get => WebUtility.UrlEncode(Name);
        set => Name = WebUtility.UrlDecode(value);
    }
    public Version Version;
    [DefaultValue(null)]
    public string Feed;
    [JsonIgnore]
    public string Url => Feed + $"/{Id}.7z";
    [DefaultValue(null)]
    public int? Index;
    [JsonIgnore]
    public bool Exists;
    public PluginId(string id)
    {
        var match = Regex.Match(id, @"^(?<id>.*?)(\.(?<version>[0-9.]+))?$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Groups["version"].Success || !Version.TryParse(match.Groups["version"].Value, out Version)) Version = null;
        Name = match.Groups["id"].Value;
    }
    public PluginId(string name, Version version) { Name = name; Version = version; }

    public static PluginId Parse(string id) => new PluginId(id);
    public static PluginId ParseEncoded(string id) => Parse(WebUtility.UrlDecode(id));
    [JsonIgnore]
    public string Id => Version != null ? $"{Name}.{Version.ToString(3)}" : Name;
    [JsonIgnore]
    public string EncodedId => Version != null ? $"{EncodedName}.{Version.ToString(3)}" : Name;
    public async Task<PluginId> NewestAsync() => await PluginManager.GetNewestPluginAsync(Id);
    public override string ToString() => Id;
    public override bool Equals(object obj)
    {
        if (obj is not PluginId) return false;
        var id = (PluginId)obj;
        return id.Name == Name && id.Version == Version;
    }
    public override int GetHashCode() => (Name?.GetHashCode() ?? 0) ^ (Version?.GetHashCode() ?? 0);
}

public class DirectoryItem
{
    public string FullName;
    public string Name;
    public string Path;
    public bool IsDirectory;

    public override bool Equals(object obj) => FullName == ((DirectoryItem)obj).FullName;
    public override int GetHashCode() => FullName.GetHashCode();
}


public class PluginManager
{
    public const string AutoDir = ".auto-installers";
    const bool DebuggingFeeds = true;
    public static List<string> Feeds = DebuggingFeeds ?
        new List<string>() {
            "https://simonegli8.github.io/HostPanelPro.Plugins",
            "http://hostpanelpro.mooo.com" } :
        new List<string>() {
            "https://simonegli8.github.io/HostPanelPro.Plugins",
            "http://hostpanelpro.mooo.com" };
    public static CancellationTokenSource Cancel = new CancellationTokenSource();
    public static HttpClientHandler Proxy { get; set; } = null;
    public static AsyncLock AsyncLock = new AsyncLock();

    public static bool IsServer
    {
        get
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            return assemblies.Any(a => a.GetName().Name == "HostPanelPro.Server");
        }
    }

    public static bool IsEnterpriseServer
    {
        get
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            return assemblies.Any(a => a.GetName().Name == "HostPanelPro.EnterpriseServer");
        }
    }
    public static bool IsPortal
    {
        get
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            return assemblies.Any(a => a.GetName().Name == "HostPanelPro.WebPortal");
        }
    }
    static HashSet<Type> calledInstallers = new HashSet<Type>();
    static HashSet<Type> calledUninstallers = new HashSet<Type>();

    #region Plugins
    public static async Task<PluginId> FindAvailablePluginAsync(string pluginId)
    {
        var id = new PluginId(pluginId);
        var file = $"/plugins/{id.Name}.7z";

        var files = Feeds
            .Select(async (feed, index) => new PluginId
            {
                Name = id.Name,
                Version = id.Version,
                Feed = feed,
                Index = index,
                Exists = await Download.FileExistsAsync(feed + file)
            })
            .ToList();
        return await TaskExtensions.WhenEach(files)
            .Select<Task<PluginId>, PluginId>(async (file, cancel) => await file)
            .Where(file => file.Exists)
            .MaxByAsync(file => file.Index);
    }
    static ConcurrentDictionary<PluginId, AsyncMutexLock> locks = new ConcurrentDictionary<PluginId, AsyncMutexLock>();
    static HashSet<PluginId> installedPlugins = new HashSet<PluginId>();

    public static async Task<IDisposable> Lock(PluginId id) => await locks.AddOrUpdate(id, id => new AsyncMutexLock($"HostPanelPro.Plugin.{id.Id}", MutexScope.Machine), (id, ulock) => ulock).LockAsync();

#if !PackAsTool
    // Raised after InstallAsync/UninstallAsync change what's on disk under ~/Plugins, so hosts can
    // drop anything they cached from plugin-contributed files (e.g. transformed App_Data configs).
    public static event Action PluginsChanged;

    public static async Task InstallAsync(params IEnumerable<string> pluginIds)
    {
        var all = Task.WhenAll(pluginIds.Select(async pluginId =>
        {
            var id = await new PluginId(pluginId).NewestAsync();
            using (var idlock = await Lock(id))
            {
                if (installedPlugins.Contains(id)) return;
                installedPlugins.Add(id);

                var file = $"/{id.EncodedId}.7z";
                if (id.Feed == null) id = await FindAvailablePluginAsync(id.Id);
                var filepath = Server.MapPath($"~/Plugins/{id.EncodedId}" + file);
                var pluginpath = filepath.Substring(0, filepath.Length - ".7z".Length);
                if (Directory.Exists(pluginpath)) return;

                // Download plugin
                var filter = (string file) => !(file.StartsWith("Server") && !IsServer ||
                        file.StartsWith("EnterpriseServer") && !IsEnterpriseServer ||
                        file.StartsWith("Portal") && !IsPortal);
                await Download.DownloadFileAndUnzipAsync(id.Url, filepath, filter: filter);
                File.Delete(filepath);

                // Handle embedded servers
                var serverpath = Path.Combine(pluginpath, "Server");
                if (Directory.Exists(serverpath))
                {
                    if (IsServer && (IsEnterpriseServer || IsPortal) && Configuration.ServerPath != null)
                    {
                        var destpath = Path.GetFullPath(Path.Combine(Server.MapPath("~"), Configuration.ServerPath, "Plugins", id.EncodedId));
                        if (Directory.Exists(destpath)) Directory.Delete(destpath, true);
                        Directory.Move(serverpath, destpath);
                    }
                }
                var eserverpath = Path.Combine(pluginpath, "EnterpriseServer");
                if (Directory.Exists(eserverpath))
                {
                    if (IsEnterpriseServer && IsPortal && Configuration.EnterpriseServerPath != null)
                    {
                        var destpath = Path.GetFullPath(Path.Combine(Server.MapPath("~"), Configuration.EnterpriseServerPath, "Plugins", id.EncodedId));
                        if (Directory.Exists(destpath)) Directory.Delete(destpath, true);
                        Directory.Move(serverpath, destpath);
                    }
                }
                var portalpath = Path.Combine(pluginpath, "Portal");
                if (!IsPortal && Directory.Exists(portalpath)) Directory.Delete(portalpath, true);

                PluginsAssemblyLoader.ResetPaths();

                // Run installers
                await SetupPlugin(pluginId);
            }
        }));
        try
        {
            await all;
        }
        catch when (all.Exception is not null)
        {
            throw all.Exception;   // AggregateException with all failures
        }
        finally
        {
            PluginsChanged?.Invoke();
        }
    }

    public static async Task UninstallAsync(string pluginId)
    {
        var id = new PluginId(pluginId);
        await SetupPlugin(id.Id, true);
        using (var idlock = await Lock(id))
        {
            if (installedPlugins.Contains(id)) installedPlugins.Remove(id);

            if (id.Version == null)
            {
                foreach (var plugin in GetInstalledPlugins()) await UninstallAsync(plugin.Id);
            }
            else
            {
                var path = Server.MapPath($"~/Plugins/{id.Id}");
                if (Directory.Exists(path))
                {
                    try
                    {
                        Directory.Delete(path, true);
                    }
                    catch { }
                }
            }

            PluginsAssemblyLoader.ResetPaths();
            PluginsChanged?.Invoke();
        }
    }

    public static PluginInfo GetInstalledPluginInfo(string pluginId) => GetInstalledPluginInfo(new PluginId(pluginId));
    public static PluginInfo GetInstalledPluginInfo(PluginId id)
    {
        var root = Server.MapPath("~/Plugins");
        var infoFiles = Directory.EnumerateFiles(Path.Combine(root, id.EncodedId, "Info"), "*.*", SearchOption.TopDirectoryOnly);
        var readmeMarkdown = infoFiles.FirstOrDefault(md => md.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var info = JsonConvert.DeserializeObject<PluginInfo>(File.ReadAllText(infoFiles.FirstOrDefault(vs => vs.EndsWith(".json"))));
        info.Id = id.Id;
        info.Name = id.Name;
        info.Image = infoFiles.FirstOrDefault(img => IsImage(img));
        info.ReadmeMarkdown = File.Exists(info.ReadmeMarkdown) ? File.ReadAllText(info.ReadmeMarkdown) : "";
        var detailsViewActionControlFile = $"{id.EncodedId}.DetailsView.ascx";
        var detailsViewActionControlPath = Path.Combine(root, id.EncodedId, "Portal", "UI", "Plugins", detailsViewActionControlFile);
        if (File.Exists(detailsViewActionControlPath))
            info.DetailsViewActionsControl = $"~/DesktopModules/HostPanelPro/Plugins/{detailsViewActionControlFile}";
        info.IsInstalled = true;
        return info;
    }

    public static IEnumerable<PluginId> GetInstalledPlugins()
    {
        // TODO add embedded server
        var root = Server.MapPath("~/Plugins");
        if (!Directory.Exists(root)) return Enumerable.Empty<PluginId>();

        return Directory.EnumerateDirectories(root, "*.*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path))
            .Where(id => !id.StartsWith("."))
            .Select(id => PluginId.ParseEncoded(id));
    }
    public static List<PluginInfo> GetAllInstalledPluginInfos() => GetInstalledPlugins()
        .Select(plugin => GetInstalledPluginInfo(plugin))
        .ToList();

    public static async Task SetupPlugin(string pluginId, bool uninstall = false)
    {
        var info = GetInstalledPluginInfo(pluginId);
        var installers = GetPluginHandlers<IPluginInstaller>(info, info.SetupAssemblies,
            type =>
            {
                if (uninstall)
                {
                    calledInstallers.Clear();
                    if (!calledUninstallers.Contains(type))
                    {
                        calledUninstallers.Add(type);
                        return true;
                    }
                }
                else
                {
                    calledUninstallers.Clear();
                    if (!calledInstallers.Contains(type))
                    {
                        calledInstallers.Add(type);
                        return true;
                    }
                }
                return false;
            });
        var tasks = installers.Select(installer => uninstall ? installer.UninstallPluginAsync() : installer.InstallPluginAsync());
        var all = Task.WhenAll(tasks);
        try
        {
            await all;
        }
        catch when (all.Exception is not null)
        {
            throw all.Exception;   // AggregateException with all failures
        }
    }

    public static async Task StartupPluginsAsync()
    {
        PluginsAssemblyLoader.Init();

        await Task.Yield();

        var infos = GetAllInstalledPluginInfos();

        var installers = infos
            .SelectMany(info => GetPluginHandlers<IPluginStartup>(info, info.StartupAssemblies));
        var tasks = installers.Select(installer => installer.StartPluginAsync());
        var all = Task.WhenAll(tasks);
        try
        {
            await all;
        }
        catch when (all.Exception is not null)
        {
            throw all.Exception;   // AggregateException with all failures
        }
    }
    public static void StartupPlugins()
    {
        PluginsAssemblyLoader.Init();

        var infos = GetAllInstalledPluginInfos();

        var installers = infos
            .SelectMany(info => GetPluginHandlers<IPluginStartup>(info, info.StartupAssemblies));
        foreach (var installer in installers)
        {
            try
            {
                installer.StartPlugin();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error starting plugin {installer.GetType().FullName}: {ex}", ex);
            }
        }
    }

#endif
    #endregion

    #region AutoInstallers
#if !PackAsTool
    public static async Task EnsureAutoInstallersAsync()
    {
        if (IsServer)
        {
            var available = await GetAutoInstallers().ToListAsync();
            var installed = new List<PluginId>();
            var auto = Server.MapPath($"~/Plugins/{AutoDir}");
            using (var @lock = await AsyncLock.LockAsync())
            {
                var autoInstallerConfig = Path.Combine(auto, "auto-installers.available.json");
                if (File.Exists(autoInstallerConfig))
                {
                    var installedJson = File.ReadAllText(autoInstallerConfig);
                    installed = JsonConvert.DeserializeObject<List<PluginId>>(installedJson);
                }
                var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, Formatting = Formatting.Indented };
                File.WriteAllText(autoInstallerConfig, JsonConvert.SerializeObject(available, settings));
            }
            var installers = available
                .Except(installed)
                .Select(plugin => InstallAutoInstallerAsync(plugin.Id));
            var todelete = installed
                .Except(available)
                .Select(plugin => UninstallAutoInstallerAsync(plugin.Id));
            var all = Task.WhenAll(installers.Concat(todelete).ToList());
            try
            {
                await all;
            }
            catch when (all.Exception is not null)
            {
                throw all.Exception;   // AggregateException with all failures
            }
        }
    }
    public static async Task InstallAutoInstallerAsync(string pluginId)
    {
        var id = await new PluginId(pluginId).NewestAsync();

        // Download plugin's auto installer
        if (id.Feed == null) id = await FindAvailablePluginAsync(id.Id);
        if (!id.Exists) throw new FileNotFoundException($"There is no plugin {id.Id} available.");

        var pluginpath = Server.MapPath($"~/Plugins/{AutoDir}/{id}/bin");

        // Delete previous AutoInstallers
        var previousDirs = Directory.EnumerateDirectories(pluginpath);
        foreach (var prevfile in previousDirs) Directory.Delete(prevfile, true);

        // Download & Unzip
        var url = id.Feed + $"/.auto-installes/{id.Id}.7z";
        await Download.DownloadFileAndUnzipAsync(url, pluginpath, null, null, null);

        PluginsAssemblyLoader.ResetPaths();
    }

    public static async Task UninstallAutoInstallerAsync(string pluginId)
    {
        var id = await new PluginId(pluginId).NewestAsync();

        var pluginpath = Server.MapPath($"~/Plugins/{AutoDir}/{id}/bin");

        // Delete AutoInstaller
        if (Directory.Exists(pluginpath))
        {
            var previousDirs = Directory.EnumerateDirectories(pluginpath);
            foreach (var prevfile in previousDirs) Directory.Delete(prevfile, true);
        }
        PluginsAssemblyLoader.ResetPaths();
    }
    public static IEnumerable<PluginId> GetInstalledAutoInstallers()
    {
        // TODO add embedded server
        var root = Server.MapPath($"~/Plugins/{AutoDir}");
        return Directory.EnumerateDirectories(root, "*.*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path))
            .Where(id => !id.StartsWith("."))
            .Select(id => PluginId.ParseEncoded(id));
    }

    public static async IAsyncEnumerable<PluginId> AutoInstallPluginsAsync(CancellationToken cancel = default)
    {
        await EnsureAutoInstallersAsync();
        var installed = new List<PluginId>();
        var available = new List<PluginId>();
        var root = Server.MapPath("~/Plugins");
        using (var @lock = await AsyncLock.LockAsync())
        {
            var autoInstallerAvailableConfig = Path.Combine(root, AutoDir, "auto-installers.available.json");
            if (File.Exists(autoInstallerAvailableConfig))
            {
                var availableJson = File.ReadAllText(autoInstallerAvailableConfig);
                available = JsonConvert.DeserializeObject<List<PluginId>>(availableJson);
            }
            var autoInstallerInstalledConfig = Path.Combine(root, AutoDir, "auto-installers.installed.json");
            if (File.Exists(autoInstallerInstalledConfig))
            {
                var installedJson = File.ReadAllText(autoInstallerInstalledConfig);
                installed = JsonConvert.DeserializeObject<List<PluginId>>(installedJson);
            }
        }

        var autoInstallers = available
            .Except(installed)
            .GroupBy(id => id.Name)
            .Select(versions => new PluginId()
            {
                Name = versions.Key,
                Version = versions
                    .OrderByDescending(version => version.Version ?? new Version(1, 0, 0))
                    .FirstOrDefault().Version
            })
            .ToList();
        var pluginsToInstall = autoInstallers
            .SelectMany(id => Directory.EnumerateDirectories(Path.Combine(root, AutoDir, id.EncodedName, "bin"))
                .Select(dir => (Id: id, Directory: dir)))
            .Reverse()
            .Where(path =>
            {
                var dir = Path.GetFileName(path.Directory);
                return OSInfo.IsNetFX && dir.StartsWith("net48") ||
                    OSInfo.IsCore &&
                    (dir == "net10.0" || dir == "net11" || dir == "net12" || dir == "net13" || dir == "netstandard2.1") ||
                    dir == "netstandard2.0";
            })
            .SelectMany(dir => Directory.EnumerateFiles(dir.Directory)
                .Select(file => (Id: dir.Id, File: file)))
            .Where(file => file.File.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                try
                {
                    return (file.Id, Assembly: Assembly.Load(file.File));
                }
                catch { return (file.Id, null); }
            })
            .SelectMany(a =>
            {
                try
                {
                    return a.Assembly?.GetExportedTypes()
                         .Where(type => type.IsAssignableFrom(typeof(IAutoInstaller)))
                         .Select(type => (Id: a.Id, Type: type))
                         ?? Array.Empty<(PluginId Id, Type Type)>();
                }
                catch { return Array.Empty<(PluginId Id, Type Type)>(); }
            })
            .Where(type => type.Type != null && type.Type.IsAssignableFrom(typeof(IAutoInstaller)))
            .Select(type =>
            {
                try
                {
                    if (type.Type != null) return (type.Id, Installer: Activator.CreateInstance(type.Type) as IAutoInstaller);
                }
                catch { }
                return (type.Id, null);
            })
            .Where(installer => installer.Installer != null)
            .Select(async installer => await installer.Installer.IsPluginRequiredAsync() ? installer.Id : default);

        var tasks = new List<Task>();
        AggregateException aex;
        await foreach (var plugin in TaskExtensions.WhenEach(pluginsToInstall)
            .Select<Task<PluginId>, PluginId>(async (plugin, cancel) => await plugin))
        {
            if (plugin.Name != null)
            {
                yield return plugin;
                tasks.Add(InstallAsync(plugin.Id));
            }
        }
        var all = Task.WhenAll(tasks);
        try
        {
            await all;
        }
        catch when (all.Exception is not null)
        {
            throw all.Exception;   // AggregateException with all failures
        }
    }
#endif
    #endregion

    static IEnumerable<DirectoryItem> ParseDirectoryListing(string html, string path, string root)
    {
        // Parse the directory listing (Apache/nginx autoindex style) for subdirectory links,
        // which correspond one-to-one with the available plugin ids.
        path = path.Trim('/') + "/";
        root = root.TrimEnd('/');
        root = $"{root}/{path}";
        var items = new HashSet<DirectoryItem>();
        var hrefRegex = new Regex(@"<a\s+href\s*=\s*[""'](?<path>[^""']+)[""']\s*(?<hpp>class\s*=\s*[""']hostpanelpro-directory-link[""']\s*)?>(?<name>.*?)</a>", RegexOptions.IgnoreCase);
        foreach (Match match in hrefRegex.Matches(html))
        {
            var href = match.Groups["path"].Value;
            var name = WebUtility.UrlDecode(match.Groups["name"].Value);
            if (!(href.EndsWith(".7z") ||
                href.EndsWith(".zip") ||
                href.EndsWith(".md") ||
                href.EndsWith(".json") ||
                href.EndsWith(".config") ||
                href.EndsWith(".svg") ||
                href.EndsWith(".svgz") ||
                href.EndsWith(".png") ||
                href.EndsWith(".webp") ||
                href.EndsWith(".gif") ||
                href.EndsWith(".jpg") ||
                href.EndsWith(".jpeg") ||
                href.EndsWith("/")) ||
                href == path || href.Contains('?') || href.Contains('#') ||
                href.StartsWith("..") || href.StartsWith("http://") || href.StartsWith("https://"))
            {
                continue;
            }

            var isDirectory = href.EndsWith("/");
            href = href.TrimEnd('/');
            var tokens = Regex.Match(href, "(?<path>^|^(?:[^/]*/)*?)(?<name>[^/]*?/?)$");
            var item = new DirectoryItem();
            item.Path = item.Name = item.FullName = null; item.IsDirectory = false;
            if (tokens.Groups["path"].Success && tokens.Groups["path"].Value.Length > 0)
            {
                item.Path = tokens.Groups["path"].Value;
            }
            if (tokens.Groups["name"].Success && tokens.Groups["name"].Value.Length > 0)
            {
                item.Name = tokens.Groups["name"].Value;
                if (item.Name.EndsWith("/"))
                {
                    item.IsDirectory = true;
                    item.Name = WebUtility.UrlDecode(item.Name.TrimEnd('/'));
                }
            }
            if (name != item.Name) continue;
            if (item.Path == null && match.Groups["hpp"].Success) item.FullName = $"{root}{item.Name}";
            else if (item.Path != null && item.Path.TrimEnd('/').EndsWith(path)) item.FullName = href;
            else continue;
            if (!items.Contains(item))
            {
                items.Add(item);
                yield return item;
            }
        }
    }

    public static async Task<IEnumerable<DirectoryItem>> GetDirectoryAsync(string url, string path)
    {
        return ParseDirectoryListing(
            (await GetStringAsync(url + path)) ?? "", path, new Uri(url).AbsolutePath);
    }
    static async Task<string> GetStringAsync(string url)
    {
        try
        {
            var handler = Proxy;
            using var client = handler != null ? new HttpClient(handler) : new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, Cancel.Token);
            if (!response.IsSuccessStatusCode) return "";
            return await response.Content.ReadAsStringAsync();
        } catch
        {
            throw;
        }
    }
    public static IAsyncEnumerable<PluginId> GetAvailablePlugins()
    {
        // Start every request up front
        var tasks = Feeds.Select(async (feed, index) => (Feed: feed, Index: index, Files: await GetDirectoryAsync(feed, "/plugins"))).ToList();

        // Consume them in completion order
        return TaskExtensions.WhenEach(tasks)
            .SelectMany(async (task, cancel) =>
                (await task).Files
                    .Where(file => !file.IsDirectory && !file.Name.StartsWith(".") && file.Name.EndsWith(".7z"))
                    .Select(file => new PluginId(file.Name.Substring(0, file.Name.Length - ".7z".Length))
                    {
                        Feed = task.Result.Feed,
                        Index = task.Result.Index,
                        Exists = true
                    }));
    }
    public static async Task<PluginId> GetNewestPluginAsync(string id)
    {
        var key = new PluginId(id);
        if (key.Version == null)
        {
            var newest = await GetAvailablePlugins()
                .Where(p => p.Name == id)
                .OrderByDescending(p => p.Version ?? new Version(1, 0, 0))
                .FirstOrDefaultAsync();

            if (newest.Name == null) throw new FileNotFoundException($"Plugin with ID {id} not found in available plugins.");
            else return newest;
        }
        else return key;

    }

    public static IAsyncEnumerable<PluginId> GetAutoInstallers()
    {
        // Start every request up front
        var tasks = Feeds.Select(async (feed, index) => (Feed: feed, Index: index, Files: await GetDirectoryAsync(feed, $"/plugins/{AutoDir}"))).ToList();

        // Consume them in completion order
        return TaskExtensions.WhenEach(tasks)
            .SelectMany(async (task, cancel) =>
                (await task).Files
                   .Where(file => !file.IsDirectory && !file.Name.StartsWith(".") && file.Name.EndsWith(".7z"))
                   .Select(entry => new PluginId
                   {
                       EncodedName = entry.Name.Substring(0, entry.Name.Length - ".7z".Length),
                       Feed = task.Result.Feed,
                       Index = task.Result.Index
                   }));
    }

    static bool IsImage(string img) => img.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    img.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                    img.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase) ||
                    img.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    img.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    img.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                    img.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    public static async IAsyncEnumerable<PluginInfo> GetAvailablePluginsInfos()
    {
        var installed = GetInstalledPluginIdsSet();

        // Start all downloads up front, then yield them in order
        var tasks = await GetAvailablePluginLinks()
            .Select(plugin => GetPluginInfoAsync(plugin, installed))
            .ToListAsync();

        foreach (var task in tasks) yield return await task;
    }

    // Only downloads the info and readme files of the requested plugin
    public static async Task<PluginInfo> GetAvailablePluginInfoAsync(string pluginId)
    {
        var plugin = await GetAvailablePluginLinks()
            .FirstOrDefaultAsync(p => p.Id.Id == pluginId);

        return plugin != null ? await GetPluginInfoAsync(plugin, GetInstalledPluginIdsSet()) : null;
    }

    class PluginLinks
    {
        public PluginId Id;
        public string Image;
        public string ReadmeMarkdown;
        public string Info;
    }

    // Lists the available plugins from the /plugins/.infos directory listings only, without downloading
    // any info or readme files. Sorted by name, so the pages stay stable between requests.
    static IAsyncEnumerable<PluginLinks> GetAvailablePluginLinks()
    {
        // Start every request up front
        var tasks = Feeds
            .Select(async (feed, index) => new
            {
                Feed = feed,
                Index = index,
                DirectoryItems = await GetDirectoryAsync(feed, "/plugins/.infos")
            })
            .ToList();

        // Consume them in completion order
        return TaskExtensions.WhenEach(tasks)
            .SelectMany(async (dir, cancel) => (await dir).DirectoryItems
                .Select(item =>
                {
                    var url = new Uri(dir.Result.Feed);
                    return new
                    {
                        dir.Result.Feed,
                        dir.Result.Index,
                        Id = PluginId.ParseEncoded(Path.GetFileNameWithoutExtension(item.Name)),
                        Link = item.FullName,
                        DowloadLink = $"{url.Scheme}://{url.Authority}{item.FullName}"
                    };
                }))
            .GroupBy(link => link.Id.Name)
            .OrderBy(plugin => plugin.Key, StringComparer.OrdinalIgnoreCase)
            .Select(plugin =>
            {
                var files = plugin
                    .OrderByDescending(link => link.Id.Version ?? new Version(1, 0, 0))
                    .ThenByDescending(link => link.Index)
                    .ToList();

                return new PluginLinks
                {
                    Id = new PluginId() { Name = plugin.Key, Version = files.FirstOrDefault()?.Id.Version },
                    Image = files.FirstOrDefault(img => IsImage(img.Link))?.DowloadLink,
                    ReadmeMarkdown = files.FirstOrDefault(md =>
                        md.Link.EndsWith(".md", StringComparison.OrdinalIgnoreCase))?.DowloadLink,
                    Info = files.FirstOrDefault(vs =>
                        vs.Link.EndsWith(".json", StringComparison.OrdinalIgnoreCase))?.DowloadLink
                };
            });
    }

    public static async Task<int> GetAvailablePluginsInfosCount() =>
        await GetAvailablePluginLinks().CountAsync();

    static HashSet<PluginId> GetInstalledPluginIdsSet() =>
#if !PackAsTool
        new HashSet<PluginId>(GetInstalledPlugins());
#else
        new HashSet<PluginId>();
#endif

    static async Task<PluginInfo> GetPluginInfoAsync(PluginLinks plugin, HashSet<PluginId> installed = null)
    {
        var get = await Task.WhenAll(GetStringAsync(plugin.ReadmeMarkdown), GetStringAsync(plugin.Info));
        var info = JsonConvert.DeserializeObject<PluginInfo>(get[1]);
        info.Id = plugin.Id.Id;
        info.Image = plugin.Image;
        info.ReadmeMarkdown = get[0];
        info.IsInstalled = installed?.Contains(plugin.Id) ?? false;
        return info;
    }

    // Only downloads the info and readme files of the plugins on the requested page. page is zero based.
    public static async IAsyncEnumerable<PluginInfo> GetAvailablePluginsInfosPaged(int page, int count)
    {
        var installed = GetInstalledPluginIdsSet();

        // Start all downloads of the page up front, then yield them in order
        var tasks = GetAvailablePluginLinks()
            .Skip(page * count)
            .Take(count)
            .Select(plugin => GetPluginInfoAsync(plugin, installed));

        await foreach (var task in tasks) yield return await task;
    }

    public static void PublishDirectoryIndex(string dir, string root)
    {
        var dirs = new DirectoryInfo(dir).EnumerateDirectories();
        var files = new DirectoryInfo(dir).EnumerateFiles()
            .Where(file => file.Name != "index.html");
        var sb = new StringBuilder(@"<html>
  <head></head>
  <body>
    <h2>Directory ");
        sb.Append(WebUtility.HtmlEncode(Path.GetFileName(dir)));
        sb.Append(@"</h2>
    <p>");
        sb.AppendLine("      <a href='..'>..</a><br/>");
        foreach (var file in dirs.OfType<FileSystemInfo>().Concat(files))
        {
            sb.AppendLine($"      <a href='{file.Name}{(file is DirectoryInfo ? "/" : "")}' " +
                $"class='hostpanelpro-directory-link'>{WebUtility.UrlEncode(file.Name)}{(file is DirectoryInfo ? "/" : "")}</a><br/>");
        }
        sb.AppendLine(@"    </p>
  </body>
</html>");
        File.WriteAllText(Path.Combine(dir, "index.html"), sb.ToString());
    }

    public static async Task PublishFromSourceToStaticWebAsync(string pluginSource, string wwwRoot)
    {
        pluginSource = pluginSource.TrimEnd(Path.DirectorySeparatorChar);
        var cwd = Environment.CurrentDirectory;
        if (pluginSource == ".") pluginSource = cwd;
        if (wwwRoot == ".") wwwRoot = cwd;
        if (!Path.IsPathRooted(pluginSource)) pluginSource = Path.GetFullPath(Path.Combine(cwd, pluginSource));
        if (!Path.IsPathRooted(wwwRoot)) wwwRoot = Path.GetFullPath(Path.Combine(cwd, wwwRoot));
        pluginSource = Path.GetFullPath(pluginSource);
        wwwRoot = Path.GetFullPath(wwwRoot);

        if (pluginSource.EndsWith("*")) // Publish all subfolders on wildcard source
        {
            var pattern = Path.GetFileName(pluginSource);
            if (pattern == "*") pattern = "*.*";
            var pluginRoot = Path.GetDirectoryName(pluginSource);
            var pluginsDir = Path.Combine(wwwRoot, "plugins");
            if (Directory.Exists(pluginsDir)) Directory.Delete(pluginsDir, true);
            foreach (var plugin in Directory.EnumerateDirectories(pluginRoot, pattern)
                .Where(p => !Path.GetFileName(p).StartsWith(".")))
            {
                await PublishFromSourceToStaticWebAsync(plugin, wwwRoot);
            }
            return;
        }

        // Create package 7z
        var id = Path.GetFileName(pluginSource);
        if (id.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            await PublishAsync(pluginSource, null, wwwRoot);
        }
        else
        {
            var files = Directory.EnumerateFiles(pluginSource, "*.*", SearchOption.AllDirectories)
                .Where(file => !Regex.IsMatch(file.Substring(pluginSource.Length),
                    $@"{Regex.Escape($"{Path.DirectorySeparatorChar}Source{Path.DirectorySeparatorChar}")}|" +
                    $@"{Regex.Escape($"{Path.DirectorySeparatorChar}Server{Path.DirectorySeparatorChar}AutoInstaller{Path.DirectorySeparatorChar}")}|" +
                    $@"{Regex.Escape($"{Path.DirectorySeparatorChar}")}\.|\.slnx$|\.csproj$|\.cs$"));
            var zip = Path.Combine(wwwRoot, "plugins", $"{id}.7z");
            Zip.Zip7zFiles(zip, pluginSource, files);
            await PublishAsync(zip, pluginSource, wwwRoot);
        }
    }

    public static DateTime GetNewestSourceFile(string path)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Exists)
        {
            var files = dir.EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !file.FullName.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            var newest = files
                .Select(file => (DateTime?)file.LastWriteTimeUtc)
                .Max()
                ?? DateTime.UtcNow;
            return newest;
        }
        return DateTime.UtcNow;
    }

    public static async Task PublishAsync(string zipFile, string source = null, string wwwRoot = null)
    {
        Console.WriteLine($"Publishing {Path.GetFileName(zipFile)}");
        var id = PluginId.ParseEncoded(Path.GetFileNameWithoutExtension(zipFile));

        var web = wwwRoot == null;
        var archive = web || source.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
        if (wwwRoot != null) wwwRoot = Path.Combine(wwwRoot, "plugins");
        if (wwwRoot.EndsWith(Path.DirectorySeparatorChar.ToString())) wwwRoot = wwwRoot.Substring(0, wwwRoot.Length - 1);
        Directory.CreateDirectory(wwwRoot);
        var temp = (archive || source == null) ? Path.Combine(Path.GetTempPath(), "HostPanelPro.Plugins", id.Id) : source;

#if !PackAsTool
        var mapPath = web ?
            (Func<string, string>)Server.MapPath :
            (path => path.Replace('/', Path.DirectorySeparatorChar).Replace("~\\plugins", wwwRoot));
#else
        Func<string, string> mapPath = path => path.Replace('/', Path.DirectorySeparatorChar).Replace("~\\plugins", wwwRoot);
#endif

        // Unzip plugin
        if (archive) Zip.Unzip7zFile(zipFile, temp);

        // Publish Infos
        var info = Path.Combine(temp, "Info");
        var destInfo = mapPath($"~/plugins/.infos");
        if (Directory.Exists(info))
        {
            Directory.CreateDirectory(destInfo);

            // get version
            var files = Directory.EnumerateFiles(info, "*.*", SearchOption.TopDirectoryOnly);
            var infosrc = files.FirstOrDefault(file => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            var plugininfo = File.Exists(infosrc) ?
                JsonConvert.DeserializeObject<PluginInfo>(File.ReadAllText(infosrc)) :
                new PluginInfo();
            plugininfo.Version = id.Version ?? new Version(1, 0, 0);
            var newestFileDate = GetNewestSourceFile(temp);
            plugininfo.Published = newestFileDate;
            if (id.Version == null) id.Version = plugininfo.Version;

            // delete old entries in ~/plugins/.infos
            foreach (var file in Directory.EnumerateFiles(destInfo, $"{id.EncodedId}.*", SearchOption.TopDirectoryOnly))
                File.Delete(file);

            // copy new entries
            var infos = files
                .Select(file => new
                {
                    File = file,
                    Extension = Path.GetExtension(file)
                })
                .GroupBy(file => file.Extension)
                .ToList();
            foreach (var file in infos)
            {
                if (file.Count() > 1) throw new NotSupportedException("Duplicate info files in archive.");
                var first = file.FirstOrDefault();
                File.Copy(first.File, Path.Combine(destInfo, $"{id.EncodedId}{first.Extension}"), true);
            }
            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, Formatting = Formatting.Indented };
            var json = JsonConvert.SerializeObject(plugininfo, settings);
            File.WriteAllText(Path.Combine(destInfo, $"{id.EncodedId}.json"), json);
        }

        // Publish auto installer
        var autoInstaller = Path.Combine(temp, "Server", "AutoInstaller", "bin");
        var destAutoInstaller = mapPath($"~/plugins/{AutoDir}/{id}.7z");
        if (Directory.Exists(autoInstaller))
        {
            Zip.Zip7zFiles(destAutoInstaller, autoInstaller);
        }

        // Move to plugin server folder
        var dest = mapPath($"~/plugins/{id.EncodedId}.7z");
        if (zipFile != dest)
        {
#if NETCOREAPP
            File.Move(zipFile, dest, true);
#else
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(zipFile, dest);
#endif
        }
        
        if (archive) Directory.Delete(temp, true);
        if (!web) // Publish index.html files
        {
            PublishDirectoryIndex(wwwRoot, "/plugins");
            foreach (var dir in Directory.EnumerateDirectories(wwwRoot, "*.*", SearchOption.AllDirectories))
                PublishDirectoryIndex(dir, dir.Replace(wwwRoot, "/plugins").Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    static IEnumerable<T> GetPluginHandlers<T>(PluginInfo plugin, string assemblies,
        Func<Type, bool> filter = null)
        where T : class
    {
        return (assemblies ??
            $"{plugin.Id}.Server.dll,{plugin.Id}.EnterpriseServer.dll,{plugin.Id}.Portal.dll")
            .Split(',', ';')
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a =>
            {
                try
                {
                    return Assembly.Load(a);
                }
                catch { return null; }
            })
            .Where(a => a != null)
            .SelectMany(a => a.GetExportedTypes())
            .Where(type => type.IsAssignableFrom(typeof(T)) && filter?.Invoke(type) != false)
            .Select(type => Activator.CreateInstance(type) as T);
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);

        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}