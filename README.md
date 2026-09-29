# HostPanelPro Plugins

A library to manage & install HostPanelPro Plugins and a tool to create a website serving as a Plugin repository.

## Usage

In order to install the tool, run
```
dotnet tool install -g HostPanelPro.Plugin.Maker
```
and then run
```
make-hpp-plugin <source> <dest>
```
where

- <source> is the root directory of a HostPanelPro Plugin project
  or the root plugin directory when it ends with an *,
  created from the HostPanelPro.Plugin dotnet template, or . 
  for the current directory.
- <dest> is the publish destination directory (the www folder) of the 
  repository that will go online.
