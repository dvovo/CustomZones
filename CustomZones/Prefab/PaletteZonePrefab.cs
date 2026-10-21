// <copyright file="PaletteZonePrefab.cs">
// CustomZones - Licensed under the MIT license.
// A ZonePrefab subclass carrying the palette marker components, mirroring
// ZoningByLaw's ByLawZonePrefab (technique reimplemented, not copied).
// </copyright>

namespace CustomZones.Prefab
{
    using Game.Prefabs;
    using System.Collections.Generic;
    using Unity.Entities;
    using CustomZones.Components;

    /// <summary>
    /// A real ZonePrefab that also declares the palette marker + allowed-
    /// building buffer in its archetype. Created via ScriptableObject.
    /// CreateInstance (never Clone) so it goes through full prefab init.
    /// </summary>
    public class PaletteZonePrefab : ZonePrefab
    {
        public override void GetPrefabComponents(HashSet<ComponentType> components)
        {
            base.GetPrefabComponents(components);
            components.Add(ComponentType.ReadWrite<PaletteZoneMarker>());
            components.Add(ComponentType.ReadWrite<PaletteAllowedBuilding>());
        }
    }
}
