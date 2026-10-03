using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Threading.Tasks;

namespace HostPanelPro.Plugins;

public interface IAutoInstaller
{
    public Task<bool> IsPluginRequiredAsync();
}

public interface IPluginInstaller
{
    public Task InstallPluginAsync();
    public Task UninstallPluginAsync();

}

public interface IPluginStartup
{
    public Task StartPluginAsync();
    public void StartPlugin();
}

public abstract class AutoInstallerBase: IAutoInstaller
{
    public abstract Task<bool> IsPluginRequiredAsync();
 }
