// <copyright file="Mod.cs">
// CustomZones - Selective zoning areas for Cities: Skylines II
// Licensed under the MIT license.
// Patterns inspired by CS2-Platter (Luca Rager, MIT) and FindIt-CSII (T. D. W., MIT).
// </copyright>

namespace CustomZones
{
    using Colossal.IO.AssetDatabase;
    using Colossal.Logging;
    using Game;
    using Game.Modding;
    using Game.Simulation;
    using CustomZones.Systems;
    using CustomZones.Utilities;

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

            // --- Systems registration ---

            // Indexes every spawnable (growable) building prefab with its
            // zone category, density, lot size etc. Runs once per game load.
            updateSystem.UpdateAfter<BuildingIndexSystem>(SystemUpdatePhase.PrefabUpdate);

            // Generates icons for the buildings the game itself can never show
            // one for (see IconGeneratorSystem). Runs on the main thread, a
            // couple of renders per frame, and caches the PNGs on disk so this
            // only ever happens once per building.
            updateSystem.UpdateAt<IconGeneratorSystem>(SystemUpdatePhase.UIUpdate);

            // Measurement spike for the "photograph the real game" pipeline.
            // Idle until the UI asks for a measurement.
            updateSystem.UpdateAt<CaptureProbeSystem>(SystemUpdatePhase.UIUpdate);

            // Turns every palette into a real paintable zone. Zone creation for
            // pre-existing palettes happens in InstallAtLoad() BELOW (ByLaw
            // timing); this registration drives the REAL-TIME mid-game creation.
            // CRITICAL: it MUST run BEFORE the vanilla ZoneSystem inside the
            // PrefabUpdate phase. PrefabSystem.OnUpdate drives that phase, and a
            // mid-game AddPrefab creates the zone entity synchronously with the
            // Created tag; if our system ran AFTER ZoneSystem, ZoneSystem would
            // miss it that frame and Created clears before the next pass (which
            // is exactly why mid-game zones stayed area=None, index=0). Running
            // before ZoneSystem lets it index the new zone in the SAME frame.
            updateSystem.UpdateBefore<PaletteZoneSystem, global::Game.Prefabs.ZoneSystem>(SystemUpdatePhase.PrefabUpdate);

            // Transfers managed data to the palette zone entity AFTER ZoneSystem
            // has assigned its index (ByLaw pattern): sets the zone flags + hash.
            updateSystem.UpdateAfter<PaletteZoneInitSystem, global::Game.Prefabs.ZoneSystem>(SystemUpdatePhase.PrefabUpdate);

            // The ByLaw-model spawner: grows ONLY each palette's whitelisted
            // buildings on its zone's cells, by demand, reusing the vanilla
            // SpawnBuildingJob. Runs just BEFORE the vanilla ZoneSpawnSystem.
            updateSystem.UpdateBefore<PaletteZoneSpawnSystem, ZoneSpawnSystem>(SystemUpdatePhase.GameSimulation);

            // UI bindings (panel, palettes submenu, filters, virtual scroll).
            updateSystem.UpdateAt<PaletteUISystem>(SystemUpdatePhase.UIUpdate);

            // CRITICAL (ByLaw): disable the vanilla ZoneCheckSystem. A whitelisted
            // building keeps its ORIGINAL zone type (we never clone/re-zone it),
            // so on a palette cell the cell's zone type != the building's zone
            // type — ZoneCheckSystem would flag that mismatch and condemn the
            // building instantly. ByLaw disables it globally for the same reason.
            updateSystem.World
                .GetOrCreateSystemManaged<global::Game.Buildings.ZoneCheckSystem>()
                .Enabled = false;

            // CRITICAL (ByLaw timing): create the category + painter + one zone
            // per palette NOW, in OnLoad. This is inside the persistent world's
            // initial prefab window where ZoneSystem's first InitializeZonePrefabs
            // pass runs over freshly-Created zone prefabs and assigns each a real
            // ZoneType index (which then persists). Creating them later missed
            // that clean window and the zones stayed area=None, index=0 forever.
            updateSystem.World
                .GetOrCreateSystemManaged<PaletteZoneSystem>()
                .InstallAtLoad();
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
