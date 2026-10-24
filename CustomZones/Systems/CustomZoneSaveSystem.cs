// <copyright file="CustomZoneSaveSystem.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones.Systems
{
    using Colossal.Serialization.Entities;
    using Game;
    using Unity.Collections;
    using Unity.Entities;

    /// <summary>
    /// Obsolete (kept so cities saved with a test build still load): a table of
    /// custom zone numbers saved with the city. The game itself maps saved zone
    /// numbers once our zone prefabs are saved (see PaletteZoneInitSystem).
    /// </summary>
    public struct CustomZoneSaveTag : IComponentData, IEmptySerializable
    {
    }

    /// <summary>Obsolete: see <see cref="CustomZoneSaveTag"/>.</summary>
    public struct CustomZoneIndexEntry : IBufferElementData, ISerializable
    {
        public FixedString64Bytes m_PaletteId;
        public ushort m_Index;

        public void Serialize<TWriter>(TWriter writer)
            where TWriter : IWriter
        {
            writer.Write(1);
            writer.Write(m_PaletteId.ToString());
            writer.Write(m_Index);
        }

        public void Deserialize<TReader>(TReader reader)
            where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out string id);
            reader.Read(out m_Index);
            m_PaletteId = new FixedString64Bytes(id ?? string.Empty);
        }
    }

    /// <summary>Removes the obsolete table from cities saved with it.</summary>
    public partial class CustomZoneSaveSystem : GameSystemBase
    {
        private EntityQuery m_SaveQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SaveQuery = GetEntityQuery(ComponentType.ReadOnly<CustomZoneSaveTag>());
            RequireForUpdate(m_SaveQuery);
        }

        protected override void OnUpdate()
        {
            EntityManager.DestroyEntity(m_SaveQuery);
        }
    }
}
