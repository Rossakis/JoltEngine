using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Voltage.Data;
using Voltage.Persistence;
using Voltage.Project;

namespace Voltage.Serialization
{
	/// <summary>
	/// Reflection-free deserializers for all engine types that are
	/// stored as JSON (scene data, entity data, prefabs, settings).
	/// These bypass JsonDecoder entirely and use JsonTokenReader for AOT safety.
	/// </summary>
	public static class AotDeserializers
	{
		#region SceneData

		public static SceneData DeserializeSceneData(string json)
		{
			using var r = new JsonTokenReader(json);
			return ReadSceneData(r);
		}

		private static SceneData ReadSceneData(JsonTokenReader r)
		{
			var data = new SceneData();
			if (!r.BeginObject()) return data;

			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "FormatVersion": data.FormatVersion = r.ReadInt(); break;
					case "Name": data.Name = r.ReadString(); break;
					case "FilePath": data.FilePath = r.ReadString(); break;
					case "CreatedAt": data.CreatedAt = r.ReadDateTime(); break;
					case "ModifiedAt": data.ModifiedAt = r.ReadDateTime(); break;
					case "ClearColor": data.ClearColor = ReadColor(r); break;
					case "LetterboxColor": data.LetterboxColor = ReadColor(r); break;
					case "ResolutionPolicy": data.ResolutionPolicy = r.ReadString(); break;
					case "DesignResolutionWidth": data.DesignResolutionWidth = r.ReadInt(); break;
					case "DesignResolutionHeight": data.DesignResolutionHeight = r.ReadInt(); break;
					case "HorizontalBleed": data.HorizontalBleed = r.ReadInt(); break;
					case "VerticalBleed": data.VerticalBleed = r.ReadInt(); break;
					case "EnablePostProcessing": data.EnablePostProcessing = r.ReadBool(); break;
					case "TiledMapFileName": data.TiledMapFileName = r.ReadString(); break;
					case "Entities": data.Entities = r.ReadList(ReadSceneEntityData); break;
					case "SceneComponents": data.SceneComponents = r.ReadList(ReadSceneComponentDataEntry); break;
					case "EditorData": data.EditorData = r.ReadStringDictionary(rd => rd.ReadString()); break;
					default: r.SkipValue(); break;
				}
			}
			return data;
		}

		#endregion

		#region SceneEntityData

		private static SceneData.SceneEntityData ReadSceneEntityData(JsonTokenReader r)
		{
			var data = new SceneData.SceneEntityData();
			if (!r.BeginObject()) return data;

			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "Id": data.Id = r.ReadGuid(); break;
					case "ParentId": data.ParentId = r.ReadNullableGuid(); break;
					case "InstanceType": data.InstanceType = r.ReadEnum<Entity.InstanceType>(); break;
					case "Name": data.Name = r.ReadString(); break;
					case "Position": data.Position = ReadVector2(r); break;
					case "Rotation": data.Rotation = r.ReadFloat(); break;
					case "Scale": data.Scale = ReadVector2(r); break;
					case "ParentEntityName": data.ParentEntityName = r.ReadString(); break;
					case "Enabled": data.Enabled = r.ReadBool(); break;
					case "UpdateOrder": data.UpdateOrder = r.ReadInt(); break;
					case "Tag": data.Tag = r.ReadInt(); break;
					case "IsSelectableInEditor": data.IsSelectableInEditor = r.ReadBool(); break;
					case "DebugRenderEnabled": data.DebugRenderEnabled = r.ReadBool(); break;
					case "OriginalPrefabName": data.OriginalPrefabName = r.ReadString(); break;
					case "OriginalPrefabGuid": data.OriginalPrefabGuid = r.ReadNullableGuid(); break;
					case "PrefabOverrides":
					{
						var list = r.ReadList(rd => rd.ReadString());
						data.PrefabOverrides = list != null ? new System.Collections.Generic.HashSet<string>(list) : null;
						break;
					}
					case "RemovedPrefabComponents": data.RemovedPrefabComponents = r.ReadList(rd => rd.ReadString()); break;
					case "EntityData": data.EntityData = r.ReadObject(ReadEntityData); break;
					default: r.SkipValue(); break;
				}
			}
			return data;
		}

		#endregion

		#region EntityData

		private static EntityData ReadEntityData(JsonTokenReader r)
		{
			var data = new EntityData();
			if (!r.BeginObject()) return data;

			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "ComponentDataList":
						data.ComponentDataList = r.ReadList(ReadComponentDataEntry);
						break;
					default: r.SkipValue(); break;
				}
			}
			return data;
		}

		#endregion

		#region ComponentDataEntry

		private static ComponentDataEntry ReadComponentDataEntry(JsonTokenReader r)
		{
			var entry = new ComponentDataEntry();
			if (!r.BeginObject()) return entry;

			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "ComponentTypeName": entry.ComponentTypeName = r.ReadString(); break;
					case "ComponentName": entry.ComponentName = r.ReadString(); break;
						case "ComponentId": entry.ComponentId = r.ReadString(); break;
					case "DataTypeName": entry.DataTypeName = r.ReadString(); break;
					case "Json": entry.Json = r.ReadString(); break;
					default: r.SkipValue(); break;
				}
			}
			return entry;
		}

		#endregion

		#region SceneComponentDataEntry

		private static SceneComponentDataEntry ReadSceneComponentDataEntry(JsonTokenReader r)
		{
			var entry = new SceneComponentDataEntry();
			if (!r.BeginObject()) return entry;

			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "ComponentTypeName": entry.ComponentTypeName = r.ReadString(); break;
					case "ComponentName":     entry.ComponentName     = r.ReadString(); break;
						case "ComponentId":       entry.ComponentId       = r.ReadString(); break;
					case "DataTypeName":      entry.DataTypeName      = r.ReadString(); break;
					case "Json":              entry.Json              = r.ReadString(); break;
					default: r.SkipValue(); break;
				}
			}
			return entry;
		}

		#endregion

		#region PrefabData

		public static PrefabData DeserializePrefabData(string json)
		{
			using var r = new JsonTokenReader(json);
			return ReadPrefabData(r);
		}

		private static PrefabData ReadPrefabData(JsonTokenReader r)
		{
			var data = new PrefabData();
			if (!r.BeginObject()) return data;

			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "Id": data.Id = r.ReadGuid(); break;
					case "InstanceType": data.InstanceType = r.ReadEnum<Entity.InstanceType>(); break;
					case "Name": data.Name = r.ReadString(); break;
					case "Rotation": data.Rotation = r.ReadFloat(); break;
					case "Scale": data.Scale = ReadVector2(r); break;
					case "EntityData": data.EntityData = r.ReadObject(ReadEntityData); break;
					case "Enabled": data.Enabled = r.ReadBool(); break;
					case "UpdateOrder": data.UpdateOrder = r.ReadInt(); break;
					case "Tag": data.Tag = r.ReadInt(); break;
					case "DebugRenderEnabled": data.DebugRenderEnabled = r.ReadBool(); break;
					case "ChildEntities":
						data.ChildEntities = r.ReadList(ReadSceneEntityData);
						break;
					default: r.SkipValue(); break;
				}
			}
			return data;
		}

		#endregion

		#region ProjectSettings

		public static ProjectSettings DeserializeProjectSettings(string json)
		{
			using var r = new JsonTokenReader(json);
			return ReadProjectSettings(r);
		}

		private static ProjectSettings ReadProjectSettings(JsonTokenReader r)
		{
			var data = new ProjectSettings();
			if (!r.BeginObject()) return data;

			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "InitialScene": data.InitialScene = r.ReadString(); break;
					case "Display": data.Display = r.ReadObject(ReadDisplaySettings); break;
					case "Audio": data.Audio = r.ReadObject(ReadAudioSettings); break;
					case "DesignResolution": data.DesignResolution = r.ReadObject(ReadDesignResolutionSettings); break;
					case "Physics": data.Physics = r.ReadObject(ReadPhysicsSettings); break;
					case "Rendering": data.Rendering = r.ReadObject(ReadRenderingSettings); break;
					case "Entities": data.Entities = r.ReadObject(ReadEntitySettings); break;
					case "ContentDirectory": data.ContentDirectory = r.ReadString(); break;
					case "AssetBuild": data.AssetBuild = r.ReadObject(ReadAssetBuildSettings); break;
					default: r.SkipValue(); break;
				}
			}
			return data;
		}

		private static ProjectSettings.AssetBuildSettings ReadAssetBuildSettings(JsonTokenReader r)
		{
			var s = new ProjectSettings.AssetBuildSettings();
			if (!r.BeginObject()) return s;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "Enabled": s.Enabled = r.ReadBool(); break;
					case "Platform": s.Platform = r.ReadString(); break;
					case "Compress": s.Compress = r.ReadBool(); break;
					case "PremultiplyAlpha": s.PremultiplyAlpha = r.ReadBool(); break;
					case "TextureFormat": s.TextureFormat = r.ReadString(); break;
					case "CompileAudio": s.CompileAudio = r.ReadBool(); break;
					case "StripSources": s.StripSources = r.ReadBool(); break;
					case "Include": s.Include = r.ReadList(rd => rd.ReadString()); break;
					case "Exclude": s.Exclude = r.ReadList(rd => rd.ReadString()); break;
					default: r.SkipValue(); break;
				}
			}
			return s;
		}

		private static ProjectSettings.DisplaySettings ReadDisplaySettings(JsonTokenReader r)
		{
			var s = new ProjectSettings.DisplaySettings();
			if (!r.BeginObject()) return s;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "ScreenWidth": s.ScreenWidth = r.ReadInt(); break;
					case "ScreenHeight": s.ScreenHeight = r.ReadInt(); break;
					case "IsFullscreen": s.IsFullscreen = r.ReadBool(); break;
					case "EnableVSync": s.EnableVSync = r.ReadBool(); break;
					default: r.SkipValue(); break;
				}
			}
			return s;
		}

		private static ProjectSettings.AudioSettings ReadAudioSettings(JsonTokenReader r)
		{
			var s = new ProjectSettings.AudioSettings();
			if (!r.BeginObject()) return s;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "MasterVolume": s.MasterVolume = r.ReadFloat(); break;
					case "MusicVolume": s.MusicVolume = r.ReadFloat(); break;
					case "SFXVolume": s.SFXVolume = r.ReadFloat(); break;
					default: r.SkipValue(); break;
				}
			}
			return s;
		}

		private static ProjectSettings.DesignResolutionSettings ReadDesignResolutionSettings(JsonTokenReader r)
		{
			var s = new ProjectSettings.DesignResolutionSettings();
			if (!r.BeginObject()) return s;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "Width": s.Width = r.ReadInt(); break;
					case "Height": s.Height = r.ReadInt(); break;
					case "ResolutionPolicy": s.ResolutionPolicy = r.ReadEnum<Scene.SceneResolutionPolicy>(); break;
					case "HorizontalBleed": s.HorizontalBleed = r.ReadInt(); break;
					case "VerticalBleed": s.VerticalBleed = r.ReadInt(); break;
					default: r.SkipValue(); break;
				}
			}
			return s;
		}

		private static ProjectSettings.PhysicsSettings ReadPhysicsSettings(JsonTokenReader r)
		{
			var s = new ProjectSettings.PhysicsSettings();
			if (!r.BeginObject()) return s;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "PhysicsLayers":
						s.PhysicsLayers = r.ReadStringDictionary(rd => rd.ReadInt());
						break;
					default: r.SkipValue(); break;
				}
			}
			return s;
		}

		private static ProjectSettings.RenderingSettings ReadRenderingSettings(JsonTokenReader r)
		{
			var s = new ProjectSettings.RenderingSettings();
			if (!r.BeginObject()) return s;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "RenderingLayers":
						s.RenderingLayers = r.ReadStringDictionary(rd => rd.ReadInt());
						break;
					case "BackgroundClearColor": s.BackgroundClearColor = ReadColor(r); break;
					default: r.SkipValue(); break;
				}
			}
			return s;
		}

		private static ProjectSettings.EntitySettings ReadEntitySettings(JsonTokenReader r)
		{
			var s = new ProjectSettings.EntitySettings();
			if (!r.BeginObject()) return s;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "EntityTags":
						s.EntityTags = r.ReadStringDictionary(rd => rd.ReadInt());
						break;
					default: r.SkipValue(); break;
				}
			}
			return s;
		}

		#endregion

		#region Primitive readers — public so source-generated deserializers can call them

		public static Vector2 ReadVector2(JsonTokenReader r)
		{
			var v = new Vector2();
			if (!r.BeginObject()) return v;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "X": v.X = r.ReadFloat(); break;
					case "Y": v.Y = r.ReadFloat(); break;
					default: r.SkipValue(); break;
				}
			}
			return v;
		}

		public static Color ReadColor(JsonTokenReader r)
		{
			int red = 0, green = 0, blue = 0, alpha = 255;
			if (!r.BeginObject()) return new Color(red, green, blue, alpha);
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "R": red = r.ReadInt(); break;
					case "G": green = r.ReadInt(); break;
					case "B": blue = r.ReadInt(); break;
					case "A": alpha = r.ReadInt(); break;
					default: r.SkipValue(); break;
				}
			}
			return new Color(red, green, blue, alpha);
		}

		/// <summary>
		/// AOT-safe reader for RectangleF. Used by source-generated ComponentData deserializers.
		/// </summary>
		public static RectangleF ReadRectangleF(JsonTokenReader r)
		{
			float x = 0, y = 0, width = 0, height = 0;
			if (!r.BeginObject()) return new RectangleF(x, y, width, height);
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "X": x = r.ReadFloat(); break;
					case "Y": y = r.ReadFloat(); break;
					case "Width": width = r.ReadFloat(); break;
					case "Height": height = r.ReadFloat(); break;
					default: r.SkipValue(); break;
				}
			}
			return new RectangleF(x, y, width, height);
		}

		/// <summary>
		/// AOT-safe reader for ComponentReference. Used by source-generated ComponentData deserializers.
		/// </summary>
		public static ComponentReference ReadComponentReference(JsonTokenReader r)
		{
			var v = new ComponentReference();
			if (!r.BeginObject()) return v;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "EntityPersistentId": v.EntityPersistentId = r.ReadString(); break;
					case "EntityName":         v.EntityName = r.ReadString(); break;
					case "ComponentId":        v.ComponentId = r.ReadString(); break;
						case "ComponentTypeName":  v.ComponentTypeName = r.ReadString(); break;
					case "ComponentName":      v.ComponentName = r.ReadString(); break;
					default: r.SkipValue(); break;
				}
			}
			return v;
		}

		/// <summary>
		/// AOT-safe reader for EntityReference. Used by source-generated ComponentData deserializers.
		/// </summary>
		public static EntityReference ReadEntityReference(JsonTokenReader r)
		{
			var v = new EntityReference();
			if (!r.BeginObject()) return v;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "EntityPersistentId": v.EntityPersistentId = r.ReadString(); break;
					case "EntityName":         v.EntityName = r.ReadString(); break;
					default: r.SkipValue(); break;
				}
			}
			return v;
		}

		/// <summary>
		/// AOT-safe reader for PrefabReference. Used by source-generated ComponentData deserializers.
		/// </summary>
		public static AssetReference ReadAssetReference(JsonTokenReader r)
		{
			var v = new AssetReference();
			if (!r.BeginObject()) return v;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "AssetGuid":
						var guidStr = r.ReadString();
						if (System.Guid.TryParse(guidStr, out var g)) v.AssetGuid = g;
						break;
					case "AssetPath": v.AssetPath = r.ReadString(); break;
					case "AssetName": v.AssetName = r.ReadString(); break;
					default: r.SkipValue(); break;
				}
			}
			return v;
		}

		public static PrefabReference ReadPrefabReference(JsonTokenReader r)
		{
			var v = new PrefabReference();
			if (!r.BeginObject()) return v;
			while (r.ReadNextKey(out var key))
			{
				switch (key)
				{
					case "PrefabGuid":
						var guidStr = r.ReadString();
						if (System.Guid.TryParse(guidStr, out var g)) v.PrefabGuid = g;
						break;
					case "PrefabPath": v.PrefabPath = r.ReadString(); break;
					case "PrefabName": v.PrefabName = r.ReadString(); break;
					default: r.SkipValue(); break;
				}
			}
			return v;
		}

		#endregion
	}
}