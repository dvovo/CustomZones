// <copyright file="PlacedByHandSystem.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones.Systems
{
    using Colossal.Collections;
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Colossal.Serialization.Entities;
    using Game;
    using Game.Buildings;
    using Game.Common;
    using Game.Prefabs;
    using Game.Tools;
    using Game.Zones;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Transform = Game.Objects.Transform;

    /// <summary>
    /// A growable building placed by hand (the game's placement tool, used by
    /// "place it in the city" and by mods such as Find It), with the zones that
    /// were under its lot then. Saved with the city.
    /// </summary>
    /// <remarks>
    /// The zone check leaves it alone while those zones stay the same, whatever
    /// they are (even none, or a zone that does not grow it). Once the zone under
    /// it is changed (repainted, erased), the game's rules apply to it again,
    /// like to any other building: it is kept if the new zone accepts it and
    /// condemned if not.
    /// </remarks>
    public struct HandPlaced : IComponentData, IQueryTypeParameter, ISerializable
    {
        /// <summary>Zones under the lot when it was placed (or last checked): <see cref="LotZones.Hash"/>.</summary>
        public int m_ZoneHash;

        public void Serialize<TWriter>(TWriter writer)
            where TWriter : IWriter
        {
            writer.Write(1);
            writer.Write(m_ZoneHash);
        }

        public void Deserialize<TReader>(TReader reader)
            where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out m_ZoneHash);
        }
    }

    /// <summary>
    /// Obsolete marker of a previous test build. It did not remember the zones
    /// under the building, so it is simply removed: those buildings follow the
    /// usual rules (the next zone change under them decides).
    /// </summary>
    public struct PlacedByHand : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }

    /// <summary>The zones under a building's lot, as one number (which zone each cell has).</summary>
    public static class LotZones
    {
        public static int Hash(
            Transform transform,
            int2 lotSize,
            NativeQuadTree<Entity, Bounds2> tree,
            ComponentLookup<Block> blocks,
            ComponentLookup<ValidArea> validAreas,
            BufferLookup<Cell> cells)
        {
            var right = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
            var forward = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
            var halfRight = right * ((lotSize.x * 0.5f) - 0.5f);
            var halfForward = forward * ((lotSize.y * 0.5f) - 0.5f);
            var extents = math.abs(halfForward) + math.abs(halfRight);

            var zones = new NativeArray<int>(lotSize.x * lotSize.y, Allocator.Temp);
            var iterator = new Iterator
            {
                m_Bounds = new Bounds2(transform.m_Position.xz - extents, transform.m_Position.xz + extents),
                m_LotSize = lotSize,
                m_StartPosition = transform.m_Position.xz + halfForward + halfRight,
                m_Right = right,
                m_Forward = forward,
                m_Zones = zones,
                m_Blocks = blocks,
                m_ValidAreas = validAreas,
                m_Cells = cells,
            };
            tree.Iterate(ref iterator);

            var hash = 17;
            for (var i = 0; i < zones.Length; i++)
            {
                hash = (hash * 31) + zones[i];
            }

            zones.Dispose();
            return hash;
        }

        private struct Iterator : INativeQuadTreeIterator<Entity, Bounds2>
        {
            public Bounds2 m_Bounds;
            public int2 m_LotSize;
            public float2 m_StartPosition;
            public float2 m_Right;
            public float2 m_Forward;
            public NativeArray<int> m_Zones;
            public ComponentLookup<Block> m_Blocks;
            public ComponentLookup<ValidArea> m_ValidAreas;
            public BufferLookup<Cell> m_Cells;

            public bool Intersect(Bounds2 bounds) => MathUtils.Intersect(bounds, m_Bounds);

            public void Iterate(Bounds2 bounds, Entity blockEntity)
            {
                if (!MathUtils.Intersect(bounds, m_Bounds) ||
                    !m_ValidAreas.TryGetComponent(blockEntity, out var validArea) ||
                    validArea.m_Area.y <= validArea.m_Area.x ||
                    !m_Blocks.TryGetComponent(blockEntity, out var block) ||
                    !m_Cells.TryGetBuffer(blockEntity, out var cells))
                {
                    return;
                }

                var rowStart = m_StartPosition;
                for (var y = 0; y < m_LotSize.y; y++)
                {
                    var position = rowStart;
                    for (var x = 0; x < m_LotSize.x; x++)
                    {
                        var cellIndex = ZoneUtils.GetCellIndex(block, position);
                        if (math.all((cellIndex >= validArea.m_Area.xz) & (cellIndex < validArea.m_Area.yw)))
                        {
                            // The zone painted on the cell, whether a building covers it or not.
                            var cell = cells[(cellIndex.y * block.m_Size.x) + cellIndex.x];
                            m_Zones[(y * m_LotSize.x) + x] = math.max(m_Zones[(y * m_LotSize.x) + x], cell.m_Zone.m_Index);
                        }

                        position -= m_Right;
                    }

                    rowStart -= m_Forward;
                }
            }
        }
    }

    /// <summary>
    /// Tags the growable buildings a tool has just created (the game adds Applied
    /// and Created to what a tool builds; buildings grown by zones have neither)
    /// with the zones under them. Runs just before the zone check, so they are
    /// spared from their first frame.
    /// </summary>
    public partial class PlacedByHandSystem : GameSystemBase
    {
        private EntityQuery m_NewQuery;
        private EntityQuery m_OldMarkerQuery;
        private Game.Zones.SearchSystem m_ZoneSearchSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ZoneSearchSystem = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            m_NewQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<Applied>(),
                    ComponentType.ReadOnly<Created>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<HandPlaced>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_OldMarkerQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PlacedByHand>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            if (m_NewQuery.IsEmpty && m_OldMarkerQuery.IsEmpty)
            {
                return;
            }

            var tree = m_ZoneSearchSystem.GetSearchTree(true, out var treeDependency);
            treeDependency.Complete();
            var blocks = GetComponentLookup<Block>(true);
            var validAreas = GetComponentLookup<ValidArea>(true);
            var cells = GetBufferLookup<Cell>(true);

            using (var entities = m_NewQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!EntityManager.HasComponent<SpawnableBuildingData>(prefab) ||
                        EntityManager.HasComponent<SignatureBuildingData>(prefab) ||
                        !EntityManager.TryGetComponent<BuildingData>(prefab, out var buildingData))
                    {
                        continue; // only growable buildings are zone-checked
                    }

                    var hash = LotZones.Hash(EntityManager.GetComponentData<Transform>(entity), buildingData.m_LotSize, tree, blocks, validAreas, cells);
                    EntityManager.AddComponentData(entity, new HandPlaced { m_ZoneHash = hash });
                    Mod.Log.Info($"Placed by hand: building {entity} is kept while the zone under it stays the same.");
                }
            }

            if (!m_OldMarkerQuery.IsEmpty)
            {
                EntityManager.RemoveComponent<PlacedByHand>(m_OldMarkerQuery);
            }

        }
    }
}
