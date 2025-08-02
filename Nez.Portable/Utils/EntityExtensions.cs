using System.Collections.Generic;

namespace Nez.Utils.Extensions
{
    public static class EntityExtensions
    {
        private static readonly Dictionary<Entity, Dictionary<string, object>> _entityTempData = new();

        /// <summary>
        /// Sets temporary data on an entity that can be retrieved later
        /// </summary>
        public static void SetData(this Entity entity, string key, object value)
        {
            if (!_entityTempData.ContainsKey(entity))
                _entityTempData[entity] = new Dictionary<string, object>();
            
            _entityTempData[entity][key] = value;
        }

        /// <summary>
        /// Gets temporary data from an entity
        /// </summary>
        public static T GetData<T>(this Entity entity, string key, T defaultValue = default(T))
        {
            if (_entityTempData.TryGetValue(entity, out var entityData) && 
                entityData.TryGetValue(key, out var value))
            {
                return (T)value;
            }
            return defaultValue;
        }

        /// <summary>
        /// Removes temporary data from an entity
        /// </summary>
        public static void RemoveData(this Entity entity, string key)
        {
            if (_entityTempData.TryGetValue(entity, out var entityData))
            {
                entityData.Remove(key);
                if (entityData.Count == 0)
                    _entityTempData.Remove(entity);
            }
        }

        /// <summary>
        /// Clears all temporary data for an entity
        /// </summary>
        public static void ClearAllData(this Entity entity)
        {
            _entityTempData.Remove(entity);
        }
    }
}