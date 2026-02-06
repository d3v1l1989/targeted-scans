using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace EmbyTargetedScan
{
    public class Plugin : BasePlugin<PluginConfiguration>
    {
        public static readonly Guid PluginId = new Guid("b2c3d4e5-f6a7-8901-bcde-f12345678901");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "TargetedScan";

        public override Guid Id => PluginId;

        public override string Description =>
            "Adds POST /Library/ScanPath endpoint for targeted library scanning without full library scans.";
    }
}
