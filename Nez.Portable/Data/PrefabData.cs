using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;

namespace Nez.Data
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public struct PrefabData
    {
        public Entity.InstanceType InstanceType;
        public string Name;
        public string EntityType;
        public float Rotation;
        public Vector2 Scale;
        public EntityData EntityData;
        public bool Enabled;
        public int UpdateOrder;
        public int Tag;
        public bool DebugRenderEnabled;
        public List<SceneData.SceneEntityData> ChildEntities = new();

		public PrefabData()
        {
            InstanceType = Entity.InstanceType.Prefab;
            Name = "";
            EntityType = "";
            Rotation = 0f;
            Scale = Vector2.One;
            EntityData = new EntityData();
            Enabled = true;
            UpdateOrder = 0;
            Tag = 0;
            DebugRenderEnabled = true;
        }

        /// <summary>
        /// Creates a PrefabData from a SceneEntityData, excluding position and parent data
        /// </summary>
        public static PrefabData FromSceneEntityData(SceneData.SceneEntityData sceneData)
        {
            return new PrefabData
            {
                InstanceType = Entity.InstanceType.Prefab,
                Name = sceneData.Name,
                EntityType = sceneData.EntityType,
                Rotation = sceneData.Rotation,
                Scale = sceneData.Scale,
                EntityData = sceneData.EntityData,
                Enabled = sceneData.Enabled,
                UpdateOrder = sceneData.UpdateOrder,
                Tag = sceneData.Tag,
                DebugRenderEnabled = sceneData.DebugRenderEnabled
            };
        }

        /// <summary>
        /// Converts this PrefabData to a SceneEntityData with default position and no parent
        /// </summary>
        public SceneData.SceneEntityData ToSceneEntityData(Vector2 position = default)
        {
            return new SceneData.SceneEntityData
            {
                InstanceType = this.InstanceType,
                Name = this.Name,
                EntityType = this.EntityType,
                Position = position,
                Rotation = this.Rotation,
                Scale = this.Scale,
                EntityData = this.EntityData,
                Enabled = this.Enabled,
                UpdateOrder = this.UpdateOrder,
                Tag = this.Tag,
                DebugRenderEnabled = this.DebugRenderEnabled,
                ParentEntityName = null
            };
        }
    }
}
