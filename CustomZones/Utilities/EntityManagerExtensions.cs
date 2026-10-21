// <copyright file="EntityManagerExtensions.cs">
// CustomZones - Licensed under the MIT license.
// </copyright>

namespace CustomZones.Utilities
{
    using Unity.Entities;

    /// <summary>
    /// The Unity.Entities version shipped with CS2 does not expose
    /// EntityManager.TryGetComponent, so we provide it as an extension
    /// (HasComponent + GetComponentData).
    /// </summary>
    public static class EntityManagerExtensions
    {
        public static bool TryGetComponent<T>(this EntityManager entityManager, Entity entity, out T component)
            where T : unmanaged, IComponentData
        {
            if (entityManager.HasComponent<T>(entity))
            {
                component = entityManager.GetComponentData<T>(entity);
                return true;
            }

            component = default;
            return false;
        }
    }
}
