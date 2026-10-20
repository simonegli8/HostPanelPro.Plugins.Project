using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace HostPanelPro.Plugins
{
    [Serializable]
    public class PluginInfo
    {
        [JsonIgnore]
        public string Id { get; set; }
        [JsonIgnore]
        public string Name { get; set; }
        [JsonIgnore]
        public string Image { get; set; }
        [JsonIgnore]
        public string ReadmeMarkdown { get; set; }
        [JsonIgnore]
        public string ReadmeHtml => !string.IsNullOrEmpty(ReadmeMarkdown) ? Markdig.Markdown.ToHtml(ReadmeMarkdown) : null;
        [DefaultValue(null)]
        public string Title { get; set; }
        [DefaultValue(null)]
        public string Description { get; set; }
        [JsonIgnore]
        public string DescriptionHtml => !string.IsNullOrEmpty(Description) ? Markdig.Markdown.ToHtml(Description) : null;
        [DefaultValue(null)]
        public string Category { get; set; }

        [DefaultValue(null)]
        public string Tags { get; set; }
        public DateTime Published { get; set; }
        [JsonConverter(typeof(Newtonsoft.Json.Converters.VersionConverter))]
        public Version Version { get; set; }
        [JsonConverter(typeof(Newtonsoft.Json.Converters.VersionConverter))]
        [DefaultValue(null)]
        public Version MinimumHostPanelProVersion { get; set; }
        [JsonConverter(typeof(Newtonsoft.Json.Converters.VersionConverter))]
        [DefaultValue(null)]
        public Version MaximumHostPanelProVersion { get; set; }
        [DefaultValue(null)]
        public string SetupAssemblies { get; set; }
        [DefaultValue(null)]
        public string StartupAssemblies { get; set; }
        [JsonIgnore]
        public bool IsInstalled { get; set; }
        [JsonIgnore]
        public string DetailsViewActionsControl { get; set; }
    }
}
