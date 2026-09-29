SET PackageVersion=1.0.0
SET Configuration=Release

del ..\..\Lib\HostPanelPro.Plugins*.nupkg
del ..\..\Lib\HostPanelPro.Plugins*.snupkg

dotnet pack -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion% -p:AssemblyVersion=%PackageVersion%
dotnet pack -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion% -p:AssemblyVersion=%PackageVersion% -p:PackToTool=true