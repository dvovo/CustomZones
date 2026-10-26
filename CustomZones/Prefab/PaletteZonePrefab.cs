// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// The zone prefab of a custom zone. The approach follows ZoningByLaw's zone
// prefab (reimplemented, not copied).

namespace CustomZones.Prefab
{
    using Game.Prefabs;
    using System.Collections.Generic;
    using Unity.Entities;
    using CustomZones.Components;

    /// <summary>
    /// A ZonePrefab whose entity also carries <see cref="PaletteZoneMarker"/> and
    /// the <see cref="PaletteAllowedBuilding"/> buffer. Created with
    /// ScriptableObject.CreateInstance (not cloned), so it goes through the
    /// game's full prefab initialisation.
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
