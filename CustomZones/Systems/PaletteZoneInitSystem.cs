// <copyright file="PaletteZoneInitSystem.cs">
// CustomZones - Licensed under the MIT license.
// Finalizes a palette zone entity once it is created, mirroring ZoningByLaw's
// ByLawZonePrefabInitSystem: it flags the zone as supporting every lot
// corner/narrow variant. Its OnUpdate firing also PROVES our zone entities
// receive the Created tag and go through init.
// </copyright>

namespace CustomZones.Systems
{
    using Colossal.Entities;
    using Game;
    using Game.Common;
    using Game.Prefabs;
    using Unity.Collections;
    using Unity.Entities;
    using CustomZones.Components;

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

                    // Support all lot corner/narrow variants, like ByLaw's init.
                    zoneData.m_ZoneFlags |= ZoneFlags.SupportLeftCorner | ZoneFlags.SupportNarrow | ZoneFlags.SupportRightCorner;
                    EntityManager.SetComponentData(entity, zoneData);
                }

                // CRITICAL for saves: the game only writes into a save the prefabs
                // that carry a Locked component (enabled or not) or are referenced
                // by saved entities. Zone cells store a zone number, and on load the
                // game maps the saved numbers to the current ones through those saved
                // zone prefabs; a zone prefab missing from the save maps to "no zone"
                // and its painted cells are wiped. Our zones have no milestone
                // unlock, so give them the component, disabled (= unlocked), like
                // every vanilla zone has.
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
