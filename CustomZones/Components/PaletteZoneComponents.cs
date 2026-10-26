// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Components carried by the zone entity of a custom zone.

namespace CustomZones.Components
{
    using Unity.Entities;

    /// <summary>
    /// Marks the zone entity of a custom zone, so the mod's systems and jobs
    /// can recognise it.
    /// </summary>
    public struct PaletteZoneMarker : IComponentData
    {
    }

    /// <summary>
    /// One building prefab that may be born on a custom zone. Kept on the zone
    /// entity so the Burst spawn job can read the zone's choice.
    /// </summary>
    public struct PaletteAllowedBuilding : IBufferElementData
    {
        public Entity m_Building;
    }
}
