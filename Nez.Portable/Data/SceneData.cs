using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;

namespace Nez.Data;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public class SceneData
{
    public string TiledMapFileName = String.Empty;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
	public List<SceneEntityData> Entities;

    public SceneData()
    {
        Entities = new List<SceneEntityData>();
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public struct SceneEntityData
    {
        public Entity.InstanceType InstanceType;
        public string Name;
        public string EntityType;
        public Vector2 Position;
        public float Rotation;
        public Vector2 Scale;
        public EntityData EntityData;
        public bool Enabled;
        public int UpdateOrder;
        public int Tag;
        public bool IsSelectableInEditor;
		public bool DebugRenderEnabled;
        public string ParentEntityName;
		public string OriginalPrefabName;

        public SceneEntityData()
        {
            InstanceType = Entity.InstanceType.HardCoded;
            Name = "";
            EntityType = "";
            Position = Vector2.Zero;
            Rotation = 0f;
            Scale = Vector2.One;
            EntityData = new EntityData();
            Enabled = true;
            UpdateOrder = 0;
            IsSelectableInEditor = true;
            Tag = 0;
            DebugRenderEnabled = true;
            ParentEntityName = null;
            OriginalPrefabName = null;
        }
    }

    public T GetEntityData<T>(string entityName) where T : EntityData, new()
    {
        // Find matching entity data by name and type
        foreach (var sceneEntity in Entities)
            if (sceneEntity.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase) &&
                sceneEntity.EntityData is T entityData)
                return entityData;

        return new T();
    }

    public Entity GetEntity(Scene scene, string name)
    {
        for (var i = 0; i < scene.Entities.Count; i++)
            foreach (var entity in Entities)
                if (entity.Name.ToLower().Equals(name.ToLower()))
                    return scene.Entities[i];


        throw new Exception($"Entity: {name} not found");
    }
}