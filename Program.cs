using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using System.Diagnostics;

namespace HostPanelPro.Plugins;

public class Program
{
    public static async Task Main(string[] args)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Console.WriteLine($"make-hpp-plugin v{version.ToString(3)}");
        var debug = args.Any(a => a == "--debug");
        if (debug) Debugger.Launch();
        args = args.Where(a => a != "--debug").ToArray();
        var source = args.FirstOrDefault();
        var dest = args.Skip(1).FirstOrDefault();
        if (source == null || dest == null || source == dest) ShowUsage();
        else
        {
            await PluginManager.PublishFromSourceToStaticWebAsync(source, dest);

            Console.WriteLine($"Sucessfully created plugin{(source.EndsWith("*") ? "s" : "")} into {dest}");
        }
    }

    public static void ShowUsage()
    {
        Console.WriteLine(@"
Usage: make-hpp-plugin <source> <dest>

where

- <source> is the root directory of a HostPanelPro Plugin project
  or the root plugin directory when it ends with an *,
  created from the HostPanelPro.Plugin dotnet template, or . 
  for the current directory.
- <dest> is the publish destination directory (the www folder) of the 
  repository that will go online, or . for the current directory.");
    }
}
