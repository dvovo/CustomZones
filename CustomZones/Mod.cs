// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Custom Zones: create your own zone types and choose exactly which growable
// buildings grow and level up in each of them, with the game's own demand.
// Some patterns are inspired by CS2-Platter (Luca Rager) and FindIt-CSII
// (T. D. W.), both MIT licensed.

namespace CustomZones
{
    using Colossal.IO.AssetDatabase;
    using Colossal.Logging;
    using Game;
    using Game.Modding;
    using Game.Simulation;
    using CustomZones.Systems;
    using CustomZones.Utilities;

    /// <summary>
    /// Entry point of Custom Zones: loads the settings and translations and
    /// registers the mod's systems.
    /// </summary>
    public class Mod : IMod
    {
        public const string Id = "CustomZones";

        public static ILog Log { get; } = LogManager.GetLogger(Id)
            .SetShowsErrorsInUI(false);

        public static Setting Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info(nameof(OnLoad));

            // Settings
            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(Id, Settings, new Setting(this));

            // Locales
            LocaleHelper.LoadEmbeddedLocales();

            // coui://customzones/ host for the generated zone icons.
            ZoneIcons.RegisterHost();

            // Indexes every growable building prefab (zone category, density,
            // lot size, level...). Rebuilt when new buildings are registered.
            updateSystem.UpdateAfter<BuildingIndexSystem>(SystemUpdatePhase.PrefabUpdate);

            // Draws a simple picture for the buildings the game has no icon for
            // (see IconGeneratorSystem). A few per frame on the main thread,
            // cached on disk, so each building is drawn only once.
            updateSystem.UpdateAt<IconGeneratorSystem>(SystemUpdatePhase.UIUpdate);

            // Turns every custom zone into a real, paintable zone prefab. The zones
            // that already exist are created in InstallAtLoad (below); this system
            // creates the ones added during a game. It must run before the game's
            // ZoneSystem: AddPrefab creates the zone entity with the Created tag,
            // and ZoneSystem only gives a zone its ZoneType index on the frame it
            // sees that tag.
            updateSystem.UpdateBefore<PaletteZoneSystem, global::Game.Prefabs.ZoneSystem>(SystemUpdatePhase.PrefabUpdate);

            // Finishes each new zone entity once ZoneSystem has indexed it: lot
            // corner flags, and what lets a saved city keep its painted cells.
            updateSystem.UpdateAfter<PaletteZoneInitSystem, global::Game.Prefabs.ZoneSystem>(SystemUpdatePhase.PrefabUpdate);

            // The zone spawner: the game's own evaluation for the game's zones, and
            // the same rules restricted to each custom zone's chosen buildings,
            // feeding the game's SpawnBuildingJob.
            updateSystem.UpdateBefore<PaletteZoneSpawnSystem, ZoneSpawnSystem>(SystemUpdatePhase.GameSimulation);

            // PaletteZoneSpawnSystem spawns for the game's zones too, from one shared
            // set of candidates, so the city grows at the game's own rate: the
            // game's spawner must not run as well.
            updateSystem.World.GetOrCreateSystemManaged<ZoneSpawnSystem>().Enabled = false;

            // Level-up rules of each custom zone (level up on or off, prefer same
            // design, skip levels) and the level buttons of the building panel,
            // applied just before BuildingConstructionSystem swaps the prefab.
            updateSystem.UpdateBefore<LevelUpControlSystem, BuildingConstructionSystem>(SystemUpdatePhase.GameSimulation);

            // UI bindings of the panel (zones, building browser, filters).
            updateSystem.UpdateAt<PaletteUISystem>(SystemUpdatePhase.UIUpdate);

            // The game's ZoneCheckSystem is replaced by CustomZoneCheckSystem. A
            // building grown on a custom zone keeps its own zone type, so the game's
            // check would condemn it at once. The replacement is the same check,
            // which also accepts a custom zone for the buildings it holds.
            updateSystem.UpdateAt<CustomZoneCheckSystem>(SystemUpdatePhase.ModificationEnd);

            // Removes an obsolete table from cities saved with a test build (zones
            // are kept by saving the zone prefabs: see PaletteZoneInitSystem).
            updateSystem.UpdateBefore<CustomZoneSaveSystem, CustomZoneCheckSystem>(SystemUpdatePhase.ModificationEnd);

            // Growable buildings placed by hand (placement tool, Find It...) are
            // tagged before the zone check sees them, so it does not condemn them.
            updateSystem.UpdateBefore<PlacedByHandSystem, CustomZoneCheckSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.World
                .GetOrCreateSystemManaged<global::Game.Buildings.ZoneCheckSystem>()
                .Enabled = false;

            // The toolbar tab and one zone per existing custom zone are created now,
            // in OnLoad, so ZoneSystem's first pass indexes them together with the
            // game's own zones and they are ready before any city loads.
            updateSystem.World
                .GetOrCreateSystemManaged<PaletteZoneSystem>()
                .InstallAtLoad();
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));

            ZoneIcons.UnregisterHost();

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
