using System;
using System.Collections.Generic;
using System.Linq;
using Voltage.Data;

namespace Voltage.Editor.Serialization;

/// <summary>Revert logic shared by the inspector and the gateway: prefab components win, instance-only components go.</summary>
internal static class PrefabOverrides
{
	public static void RevertAll(Entity entity, SceneData.SceneEntityData record, PrefabData data)
	{
		if (record != null)
		{
			record.PrefabOverrides = new HashSet<string>();
			record.RemovedPrefabComponents = null;
			record.EntityData = new EntityData();
		}
		Apply(entity, data);
	}

	public static void RevertComponent(Entity entity, SceneData.SceneEntityData record, PrefabData data, string componentName)
	{
		var entry = data.EntityData?.ComponentDataList?.FirstOrDefault(e => string.Equals(e.ComponentName, componentName, StringComparison.Ordinal));
		if (record != null)
		{
			record.PrefabOverrides?.Remove(componentName);
			record.RemovedPrefabComponents?.Remove(componentName);
			record.EntityData?.ComponentDataList?.RemoveAll(e => string.Equals(e.ComponentName, componentName, StringComparison.Ordinal));
		}

		var live = FindLive(entity, componentName);
		if (entry.HasValue && !string.IsNullOrEmpty(entry.Value.ComponentTypeName))
		{
			live ??= AddFromEntry(entity, entry.Value);
			if (live != null && !string.IsNullOrEmpty(entry.Value.DataTypeName) && !string.IsNullOrEmpty(entry.Value.Json))
				SerializationManager.Instance.ApplyComponentEntry(live, entry.Value);
		}
		else if (live != null)
			entity.RemoveComponent(live);
	}

	public static void Apply(Entity entity, PrefabData data)
	{
		if (data.EntityData?.ComponentDataList == null)
			return;

		foreach (var entry in data.EntityData.ComponentDataList)
		{
			var live = FindLive(entity, entry.ComponentName) ?? AddFromEntry(entity, entry);
			if (live != null && !string.IsNullOrEmpty(entry.DataTypeName) && !string.IsNullOrEmpty(entry.Json))
				SerializationManager.Instance.ApplyComponentEntry(live, entry);
		}

		var names = new HashSet<string>(data.EntityData.ComponentDataList.Select(e => e.ComponentName), StringComparer.Ordinal);
		foreach (var extra in entity.Components.Concat(entity.ComponentsToAdd).Where(c => c.IsSerialized && !names.Contains(c.Name)).ToList())
			entity.RemoveComponent(extra);
	}

	public static Component FindLive(Entity entity, string name) =>
		entity.Components.Concat(entity.ComponentsToAdd).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

	public static Component AddFromEntry(Entity entity, ComponentDataEntry entry)
	{
		var component = SerializationManager.CreateComponentInstancePublic(entry.ComponentTypeName, entry.ComponentId);
		if (component == null)
			return null;
		component.Name = entry.ComponentName;
		component.SetSerialized(true);
		entity.AddComponent(component, true);
		return component;
	}
}
