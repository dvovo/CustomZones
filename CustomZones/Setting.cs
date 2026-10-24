// <copyright file="Setting.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones
{
    using Colossal.IO.AssetDatabase;
    using Game.Modding;
    using Game.Settings;

    [FileLocation("ModsSettings/" + Mod.Id + "/" + Mod.Id)]
    [SettingsUIGroupOrder(kGeneralGroup)]
    [SettingsUIShowGroupName(kGeneralGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kGeneralGroup = "General";

        public Setting(IMod mod) : base(mod)
        {
        }

        public override void SetDefaults()
        {
        }
    }
}
