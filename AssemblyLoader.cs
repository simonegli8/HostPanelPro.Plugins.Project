#if !PackAsTool

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using System.Runtime.InteropServices;
using System.Configuration.Assemblies;
using HostPanelPro.Web.Services;
using HostPanelPro.Plugins;



#if NETFRAMEWORK
using System.Configuration;
#endif

using HostPanelPro.Providers.OS;

namespace HostPanelPro.Plugins;

public class PluginsAssemblyLoader
{
    public const bool CreateShadowCopies = Web.Clients.AssemblyLoader.CreateShadowCopies;
    public static int Initialized = 0;
    public static void Init()
    {
        if (Interlocked.Exchange(ref Initialized, 1) == 1) return;
    }

    public static string TempFile(string file) => Web.Clients.AssemblyLoader.TempFile(file);
    static IEnumerable<string> paths = null;
    public static void ResetPaths() => paths = null;
    public static IEnumerable<string> Paths
    {
        get
        {
            if (paths != null) return paths;
            var root = Server.MapPath("~");
            var roots = new List<string> { Path.Combine(root, "Plugins") };
            if (!string.IsNullOrEmpty(Configuration.EnterpriseServerPath))
            {
                roots.Add(Path.GetFullPath(Path.Combine(root, Configuration.EnterpriseServerPath, "Plugins")));
            }
            if (!string.IsNullOrEmpty(Configuration.ServerPath))
            {
                roots.Add(Path.GetFullPath(Path.Combine(root, Configuration.ServerPath, "Plugins")));
            }
            var plugins = PluginManager.GetInstalledPlugins();
            var autoInstallers = PluginManager.GetInstalledAutoInstallers();
            var bins = roots
                .SelectMany(root => plugins
                    .SelectMany(id => new[] {
                        Path.Combine(root, id.EncodedId, "Server", "bin"),
                        Path.Combine(root, id.EncodedId, "EnterpriseServer", "bin"),
                        Path.Combine(root, id.EncodedId, "Portal", "bin"),
                        Path.Combine(root, id.EncodedId, "Server", "AutoInstaller", "bin")
                    }))
                .Concat(autoInstallers
                    .Select(id => Path.Combine(root, "Plugins", PluginManager.AutoDir, id.EncodedId, "bin"))
                .Where(path => Directory.Exists(path)));
            var folders = new List<string>();
            if (OSInfo.IsNetFX)
            {
                if (OSInfo.NetFXVersion > new Version(4, 8)) folders.Add("net481");
                folders.Add("net48");
            }
            else if (OSInfo.IsCore)
            {
                const int minimumVersion = 8;
                for (int major = OSInfo.NetVersion.Major; major > minimumVersion; major--)
                {
                    folders.Add($"net{major}.0");
                }
                folders.Add("netstandard2.1");
            }
            folders.Add("netstandard2.0");
            return paths = bins
                 .SelectMany(bin => folders
                     .Select(folder => Path.Combine(bin, folder)))
                 .Where(path => Directory.Exists(path))
                 .ToList();
        }
    }
    public static Dictionary<string, Assembly> LoadedAssemblies => Web.Clients.AssemblyLoader.LoadedAssemblies;
    public static ConcurrentDictionary<string, object> Locks => Web.Clients.AssemblyLoader.Locks;
    public static ConcurrentDictionary<string, string> OriginalFiles => Web.Clients.AssemblyLoader.OriginalFiles;
    public static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;

        var lockobj = Locks.GetOrAdd(name, new object());

        lock (lockobj)
        {
            Assembly loadedAssembly;
            if (LoadedAssemblies.TryGetValue(name, out loadedAssembly)) return loadedAssembly;

            loadedAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (loadedAssembly != null)
            {
                LoadedAssemblies.Add(name, loadedAssembly);
                return loadedAssembly;
            }

            var dlls = Paths
            .Select(p =>
            {
                var relativename = Path.Combine(p, $"{name}.dll");
                var fullName = Path.GetFullPath(Path.Combine(Server.MapPath("~"), relativename));
                return new
                {
                    FullName = fullName,
                    Name = relativename
                };
            })
            .Where(p => File.Exists(p.FullName));

            foreach (var p in dlls)
            {
                string file = null;
                string originalFile = null;
                // Create shadow copy
                if (OSInfo.IsWindows && CreateShadowCopies)
                {

                    originalFile = p.FullName;
                    var temp = TempFile(originalFile);
                    try
                    {
                        File.Copy(p.FullName, temp, true);
                        var pdb = Path.ChangeExtension(p.FullName, ".pdb");
                        if (File.Exists(pdb)) File.Copy(pdb, Path.ChangeExtension(temp, $".pdb"), true);
                        file = temp;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Cannot load assembly {temp} because it's used by another process: {ex}");
                    }
                }
                else file = originalFile = p.FullName;

                OriginalFiles.AddOrUpdate(file, originalFile, (fileName, originalFileName) => originalFile);

                // Load assembly
                var a = Assembly.LoadFrom(file);
                if (a != null)
                {
                    LoadedAssemblies.Add(name, a);

                    var msg = $"Loaded assembly {p.Name}";
                    if (OSInfo.IsWindows) msg += $" from {file}";
                    Console.WriteLine(msg);
                    if (Debugger.IsAttached) Debugger.Log(1, "info", $"{msg}\r\n");
                }
                return a;
            }

            return null;
        }
    }
}

#endif