// <copyright file="PaletteZoneComponents.cs">
// CustomZones - Licensed under the MIT license.
// Marker components carried by a palette's zone entity (ByLaw-style).
// </copyright>

namespace CustomZones.Components
{
    using Unity.Entities;

    /// <summary>
    /// Tag identifying a runtime palette zone, so our init/spawn systems can
    /// recognize it (the spawn job only checks for its presence).
    /// </summary>
    public struct PaletteZoneMarker : IComponentData
    {
    }

    /// <summary>
    /// One allowed (whitelisted) building prefab entity for a palette zone.
    /// Populated on the zone entity so a Burst spawn job can filter by it.
    /// </summary>
    public struct PaletteAllowedBuilding : IBufferElementData
    {
        public Entity m_Building;
    }
}
