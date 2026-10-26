// <copyright file="Setting.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones
{
    using System;
    using System.IO;
    using Colossal.IO.AssetDatabase;
    using Game.Modding;
    using Game.Settings;
    using CustomZones.Storage;

    /// <summary>The mod's page in the game's options.</summary>
    [FileLocation("ModsSettings/" + Mod.Id + "/" + Mod.Id)]
    [SettingsUIGroupOrder(kBuildingsGroup, kInterfaceGroup, kFilesGroup)]
    [SettingsUIShowGroupName(kBuildingsGroup, kInterfaceGroup, kFilesGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kBuildingsGroup = "Buildings";
        public const string kInterfaceGroup = "Interface";
        public const string kFilesGroup = "Files";

        public Setting(IMod mod)
            : base(mod)
        {
            SetDefaults();
        }

        /// <summary>
        /// Growable buildings placed by hand (placement tool, Find It...) stay while
        /// the zone under them is the one they were placed on. Off: they follow the
        /// zone rules like any other building.
        /// </summary>
        [SettingsUISection(kSection, kBuildingsGroup)]
        public bool KeepHandPlacedBuildings { get; set; }

        /// <summary>The "level up" and "choose level" buttons in the building info panel.</summary>
        [SettingsUISection(kSection, kInterfaceGroup)]
        public bool ShowLevelControls { get; set; }

        /// <summary>Opens the folder with the exported zones.</summary>
        [SettingsUIButton]
        [SettingsUISection(kSection, kFilesGroup)]
        public bool OpenExportsFolder
        {
            set
            {
                try
                {
                    var folder = PaletteStorage.Instance.ExportFolder;
                    Directory.CreateDirectory(folder);
                    UnityEngine.Application.OpenURL(new Uri(folder).AbsoluteUri);
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn(ex, "Could not open the exports folder.");
                }
            }
        }

        /// <summary>Removes every icon saved from the icon designer.</summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kFilesGroup)]
        public bool ClearIconLibrary
        {
            set => IconLibrary.Instance.Clear();
        }

        public override void SetDefaults()
        {
            KeepHandPlacedBuildings = true;
            ShowLevelControls = true;
        }
    }
}
