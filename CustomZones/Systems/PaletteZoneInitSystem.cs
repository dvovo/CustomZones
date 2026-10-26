// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Finishes the zone entity of each custom zone when it is created. The approach
// follows ZoningByLaw's zone init system (reimplemented, not copied).

namespace CustomZones.Systems
{
    using Colossal.Entities;
    using Game;
    using Game.Common;
    using Game.Prefabs;
    using Unity.Collections;
    using Unity.Entities;
    using CustomZones.Components;

    /// <summary>
    /// Finishes each new custom zone entity: it supports every lot corner and
    /// narrow lot variant, and carries a disabled Locked component so the city
    /// saves it (see below).
    /// </summary>
    public partial class PaletteZoneInitSystem : GameSystemBase
    {
        private EntityQuery m_CreatedQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CreatedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Created>(),
                ComponentType.ReadWrite<PaletteZoneMarker>());
            RequireForUpdate(m_CreatedQuery);
        }

        protected override void OnUpdate()
        {
            var entities = m_CreatedQuery.ToEntityArray(Allocator.Temp);

            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];

                if (EntityManager.TryGetComponent<ZoneData>(entity, out var zoneData))
                {
                    Mod.Log.Info($"PaletteZoneInit entity={entity.Index}: areaType={zoneData.m_AreaType}, zoneTypeIndex={zoneData.m_ZoneType.m_Index}.");

                    // Every lot corner and narrow lot variant.
                    zoneData.m_ZoneFlags |= ZoneFlags.SupportLeftCorner | ZoneFlags.SupportNarrow | ZoneFlags.SupportRightCorner;
                    EntityManager.SetComponentData(entity, zoneData);
                }

                // Needed for saves: the game only writes into a save the prefabs
                // that carry a Locked component (enabled or not) or are referenced
                // by saved entities. Zone cells store a zone number, and on load the
                // game maps the saved numbers to the current ones through those saved
                // zone prefabs; a zone prefab missing from the save maps to "no zone"
                // and its painted cells are lost. Custom zones have no milestone, so
                // they get the component disabled (unlocked), as the game's zones
                // have it once unlocked.
                if (!EntityManager.HasComponent<Locked>(entity))
                {
                    EntityManager.AddComponent<Locked>(entity);
                }

                EntityManager.SetComponentEnabled<Locked>(entity, false);
            }

            entities.Dispose();
        }
    }
}
