// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

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
