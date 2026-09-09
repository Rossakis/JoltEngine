using System;
using System.Collections.Generic;
using System.Linq;
using Voltage.Data;
using Voltage.Editor.Serialization;
using Voltage.Editor.Undo.Core;

namespace Voltage.Editor.Undo.PrefabActions
{
	/// <summary>Reverts a prefab instance to its prefab; undo restores the instance's components and scene record as they were.</summary>
	public class PrefabRevertUndoAction : EditorChangeTracker.IEditorAction
	{
		private readonly Entity _entity;
		private readonly SceneData.SceneEntityData _record;
		private readonly PrefabData _prefab;
		private readonly string _componentName;
		private readonly List<ComponentDataEntry> _entries;
		private readonly HashSet<string> _overrides;
		private readonly List<string> _removed;
		private readonly EntityData _entityData;

		public string Description => _componentName == null
			? $"Revert {_entity?.Name} to prefab {_entity?.OriginalPrefabName}"
			: $"Revert {_componentName} on {_entity?.Name} to prefab";

		/// <summary>Snapshots the live components and record before the revert runs.</summary>
		public PrefabRevertUndoAction(Entity entity, SceneData.SceneEntityData record, PrefabData prefab, string componentName)
		{
			_entity = entity;
			_record = record;
			_prefab = prefab;
			_componentName = componentName;
			_entries = entity.Components.Concat(entity.ComponentsToAdd)
				.Where(c => c.IsSerialized)
				.Select(SerializationManager.SerializeComponentEntry)
				.Where(e => e.HasValue)
				.Select(e => e.Value)
				.ToList();
			_overrides = record?.PrefabOverrides == null ? null : new HashSet<string>(record.PrefabOverrides);
			_removed = record?.RemovedPrefabComponents == null ? null : new List<string>(record.RemovedPrefabComponents);
			_entityData = record?.EntityData == null ? null : new EntityData { ComponentDataList = new List<ComponentDataEntry>(record.EntityData.ComponentDataList ?? new List<ComponentDataEntry>()) };
		}

		public void Undo()
		{
			if (_entity == null || _entity.IsDestroyed)
				return;

			if (_record != null)
			{
				_record.PrefabOverrides = _overrides == null ? null : new HashSet<string>(_overrides);
				_record.RemovedPrefabComponents = _removed == null ? null : new List<string>(_removed);
				_record.EntityData = _entityData == null ? null : new EntityData { ComponentDataList = new List<ComponentDataEntry>(_entityData.ComponentDataList) };
			}

			foreach (var entry in _entries)
			{
				var live = PrefabOverrides.FindLive(_entity, entry.ComponentName) ?? PrefabOverrides.AddFromEntry(_entity, entry);
				if (live != null && !string.IsNullOrEmpty(entry.DataTypeName) && !string.IsNullOrEmpty(entry.Json))
					SerializationManager.Instance.ApplyComponentEntry(live, entry);
			}

			var names = new HashSet<string>(_entries.Select(e => e.ComponentName), StringComparer.Ordinal);
			foreach (var extra in _entity.Components.Concat(_entity.ComponentsToAdd).Where(c => c.IsSerialized && !names.Contains(c.Name)).ToList())
				_entity.RemoveComponent(extra);
		}

		public void Redo()
		{
			if (_entity == null || _entity.IsDestroyed)
				return;
			if (_componentName == null)
				PrefabOverrides.RevertAll(_entity, _record, _prefab);
			else
				PrefabOverrides.RevertComponent(_entity, _record, _prefab, _componentName);
		}
	}
}
