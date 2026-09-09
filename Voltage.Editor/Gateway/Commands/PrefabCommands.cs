using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Voltage.Data;
using Voltage.Editor.Assets;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Serialization;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.EntityActions;
using Voltage.Gateway;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Prefab authoring: create from an entity, instantiate, apply and revert overrides, and the isolated edit scene.</summary>
internal static class PrefabCommands
{
	private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(30);

	public static void Register(GatewayCommandTable table)
	{
		table.Add("prefab.list", "Prefab assets of the project.", (_, _) =>
			RequireAssets().Items
				.Where(i => i.Descriptor.Kind == AssetKind.Prefab)
				.DistinctBy(i => i.AbsolutePath, StringComparer.OrdinalIgnoreCase)
				.Select(i => new { name = Path.GetFileNameWithoutExtension(i.FileName), path = i.AbsolutePath, guid = AssetDatabase.Instance.GetReference(i.AbsolutePath).Guid })
				.ToList()).ReadOnly();

		table.Add("prefab.info", "Which prefab an entity instances, its asset, and the component overrides recorded in the scene.", (args, _) =>
			Info(ResolveEntity(args.Require("entity"))), P.Str("entity", "Entity id, GUID or name", required: true)).ReadOnly();

		table.Add("prefab.create", "Save an entity as a new .vprefab the way the inspector's Save as Prefab does: a linked copy of the entity becomes the instance; the original is kept unless keepOriginal=false.", (args, ctx) =>
		{
			var scene = RequireScene();
			var project = RequireProject();
			var entity = ResolveEntity(args.Require("entity"));
			var name = args.String("name", entity.Name).Trim();
			if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new GatewayException($"invalid prefab name '{name}'");

			var path = Path.Combine(project.PrefabsFolder, $"{name}.vprefab");
			var overwrite = args.Bool("overwrite");
			if (File.Exists(path) && !overwrite)
				throw new GatewayException($"prefab already exists: {path}; pass overwrite=true");

			var pane = ctx.ImGui().SceneGraphWindow?.EntityPane ?? throw new GatewayException("scene graph window is not available");
			var instance = pane.DuplicateEntity(entity, name) ?? throw new GatewayException("the entity could not be duplicated");
			instance.Type = Entity.InstanceType.SerializedPrefab;
			instance.Name = name;
			instance.OriginalPrefabName = name;

			var keepOriginal = args.Bool("keepOriginal", true);
			return Await(SerializationManager.Instance.SavePrefabDataAsync(instance, overwrite), saved =>
			{
				if (!saved)
					throw new GatewayException("prefab save failed; see log.tail");

				ctx.ImGui().SceneGraphWindow.AddPrefabToCache(name);
				AssetDatabase.Instance?.Refresh();
				if (!keepOriginal)
				{
					var description = $"Delete Entity {entity.Name}";
					pane.SelectedEntities.Remove(entity);
					EditorChangeTracker.PushUndo(new EntityCreateDeleteUndoAction(scene, entity, wasCreated: false, description), entity, description);
					entity.Destroy();
				}
				return new { path, guid = AssetDatabase.Instance?.GetReference(path).Guid, instance = EntityCommands.Detail(instance) };
			});
		}, P.Str("entity", "Entity id, GUID or name", required: true), P.Str("name", "Prefab name; default the entity name"), P.Bool("overwrite", "Replace an existing prefab file", false), P.Bool("keepOriginal", "Keep the source entity next to the new instance", true));

		table.Add("prefab.instantiate", "Place an instance of a prefab asset in the scene (undoable).", (args, ctx) =>
		{
			RequireScene();
			var item = ResolveAsset(args.Require("asset"));
			if (item.Descriptor.Kind != AssetKind.Prefab)
				throw new GatewayException($"{item.FileName} is not a prefab");

			var data = SerializationManager.Instance.LoadPrefabDataFromPath(item.AbsolutePath) ?? throw new GatewayException("prefab data could not be read");
			var reference = AssetDatabase.Instance.GetReference(item.AbsolutePath);
			Vector2? position = args.Has("x") || args.Has("y") ? new Vector2(args.Float("x"), args.Float("y")) : null;
			var entity = ctx.ImGui().SceneGraphWindow.CreateEntityFromPrefabData(data, Path.GetFileNameWithoutExtension(item.FileName), reference.Guid, position)
				?? throw new GatewayException("instantiation failed; see log.tail");
			return EntityCommands.Detail(entity);
		}, P.Str("asset", "Prefab GUID, path or file name", required: true), P.Float("x", "World position; default the view centre"), P.Float("y"));

		table.Add("prefab.apply", "Write an instance's current state back to its .vprefab and, with copies=true, onto the other instances in the scene (the copies step is undoable).", (args, ctx) =>
		{
			var entity = RequireInstance(ResolveEntity(args.Require("entity")));
			var toAsset = args.Bool("asset", true);
			var toCopies = args.Bool("copies", true);
			var copies = 0;
			if (toCopies)
			{
				copies = Copies(entity).Count;
				ctx.ImGui().MainEntityInspectorWindow?.ApplyEntityToPrefabCopies(entity);
			}

			if (!toAsset)
				return new { asset = false, copies };

			return Await(SerializationManager.Instance.InvokePrefabCreated(entity, true), saved =>
			{
				if (!saved)
					throw new GatewayException("prefab save failed; see log.tail");
				return new { asset = PrefabPath(entity), copies };
			});
		}, P.Str("entity", "Prefab instance id, GUID or name", required: true), P.Bool("asset", "Save the instance into the prefab file", true), P.Bool("copies", "Apply to the other instances in the scene", true));

		table.Add("prefab.revert", "Discard an instance's overrides and reload its components from the prefab, for one component or all of them.", (args, ctx) =>
		{
			var entity = RequireInstance(ResolveEntity(args.Require("entity")));
			var component = args.String("component");
			var data = LoadPrefabData(entity) ?? throw new GatewayException($"prefab '{entity.OriginalPrefabName}' could not be loaded");
			var record = SceneRecord(entity);

			if (string.IsNullOrEmpty(component))
				PrefabOverrides.RevertAll(entity, record, data);
			else
				PrefabOverrides.RevertComponent(entity, record, data, component);

			EditorChangeTracker.MarkChanged(entity, $"Revert {entity.Name} to prefab {entity.OriginalPrefabName}");
			ctx.ImGui().MainEntityInspectorWindow?.DelayedSetEntity(entity);
			return Info(entity);
		}, P.Str("entity", "Prefab instance id, GUID or name", required: true), P.Str("component", "Component name to revert; default all")).Destructive();

		table.Add("prefab.open", "Open a prefab in the isolated edit scene, as double-clicking it in the Asset Browser does. Refuses to leave unsaved scene changes unless force=true.", (args, ctx) =>
		{
			var manager = ctx.ImGui();
			if (manager.IsInPrefabEditScene)
				throw new GatewayException($"already editing prefab '{manager.PrefabEditName}'; call prefab.close first");
			if (EditorChangeTracker.IsDirty && !args.Bool("force"))
				throw new GatewayException("scene has unsaved changes; call scene.save or pass force=true");

			string path;
			if (args.Has("entity"))
				path = PrefabPath(RequireInstance(ResolveEntity(args.Require("entity")))) ?? throw new GatewayException("the instance's prefab file was not found");
			else
			{
				var item = ResolveAsset(args.Require("asset"));
				if (item.Descriptor.Kind != AssetKind.Prefab)
					throw new GatewayException($"{item.FileName} is not a prefab");
				path = item.AbsolutePath;
			}

			var data = SerializationManager.Instance.LoadPrefabDataFromPath(path) ?? throw new GatewayException("prefab data could not be read");
			var name = Path.GetFileNameWithoutExtension(path);
			// The manager queues its own unsaved-changes prompt while the tracker is dirty; force means discard.
			EditorChangeTracker.Clear();
			manager.OpenPrefabIsolated(data, name, AssetDatabase.Instance.GetReference(path).Guid);

			return GatewayTasks.WhenReady(() => manager.IsInPrefabEditScene, () => new { editing = manager.IsInPrefabEditScene, prefab = manager.PrefabEditName, path });
		}, P.Str("asset", "Prefab GUID, path or file name"), P.Str("entity", "A prefab instance whose prefab to open"), P.Bool("force", "Discard unsaved scene changes", false)).Destructive();

		table.Add("prefab.close", "Leave the prefab edit scene and return to the previous scene, saving the prefab first with save=true. Refuses to drop unsaved prefab edits unless save=true or force=true.", (args, ctx) =>
		{
			var manager = ctx.ImGui();
			if (!manager.IsInPrefabEditScene)
				throw new GatewayException("not in a prefab edit scene");

			var save = args.Bool("save");
			if (EditorChangeTracker.IsDirty && !save && !args.Bool("force"))
				throw new GatewayException("the prefab has unsaved edits; pass save=true or force=true");

			var name = manager.PrefabEditName;
			if (!save)
			{
				manager.ExitPrefabEditScene();
				return new { prefab = name, saved = false };
			}

			var instance = Core.Scene?.Entities.FirstOrDefault(e => e.Type == Entity.InstanceType.SerializedPrefab && e.OriginalPrefabName == name)
				?? throw new GatewayException("the edited prefab instance was not found in the scene");
			return Await(SerializationManager.Instance.InvokePrefabCreated(instance, true), saved =>
			{
				if (!saved)
					throw new GatewayException("prefab save failed; see log.tail");
				EditorChangeTracker.Clear();
				manager.ExitPrefabEditScene();
				return new { prefab = name, saved = true };
			});
		}, P.Bool("save", "Write the edits to the .vprefab before leaving", false), P.Bool("force", "Discard unsaved prefab edits", false)).Destructive();
	}

	private static Entity RequireInstance(Entity entity)
	{
		if (entity.Type != Entity.InstanceType.SerializedPrefab || string.IsNullOrEmpty(entity.OriginalPrefabName))
			throw new GatewayException($"{entity.Name} is not a prefab instance");
		return entity;
	}

	/// <summary>Bridges an async editor save into the dispatcher's deferred-reply path.</summary>
	private static Task<object> Await(Task<bool> save, Func<bool, object> then) =>
		GatewayTasks.FromTask(save, then, (float)SaveTimeout.TotalSeconds);

	private static List<Entity> Copies(Entity source) =>
		Core.Scene.Entities.Where(e => e != source && e.Type == Entity.InstanceType.SerializedPrefab && e.OriginalPrefabName == source.OriginalPrefabName).ToList();

	private static string PrefabPath(Entity entity)
	{
		var path = Scene.PrefabPathResolver?.Invoke(entity.OriginalPrefabGuid, entity.OriginalPrefabName);
		if (!string.IsNullOrEmpty(path) && File.Exists(path))
			return path;
		var project = ProjectManager.Instance.CurrentProject;
		if (project == null)
			return null;
		var direct = Path.Combine(project.PrefabsFolder, $"{entity.OriginalPrefabName}.vprefab");
		return File.Exists(direct) ? direct : null;
	}

	private static PrefabData? LoadPrefabData(Entity entity)
	{
		var path = PrefabPath(entity);
		if (path == null)
			return null;
		try { return SerializationManager.Instance.LoadPrefabDataFromPath(path); }
		catch (Exception) { return null; }
	}

	private static SceneData.SceneEntityData SceneRecord(Entity entity) =>
		Core.Scene?.SceneData?.Entities?.FirstOrDefault(e => string.Equals(e.Name, entity.Name, StringComparison.OrdinalIgnoreCase));

	private static object Info(Entity entity)
	{
		var isInstance = entity.Type == Entity.InstanceType.SerializedPrefab && !string.IsNullOrEmpty(entity.OriginalPrefabName);
		var record = isInstance ? SceneRecord(entity) : null;
		return new
		{
			entity = entity.Id,
			name = entity.Name,
			isInstance,
			prefab = isInstance ? entity.OriginalPrefabName : null,
			guid = isInstance && entity.OriginalPrefabGuid != Guid.Empty ? entity.OriginalPrefabGuid : (Guid?)null,
			path = isInstance ? PrefabPath(entity) : null,
			overrides = record?.PrefabOverrides?.ToList(),
			removedComponents = record?.RemovedPrefabComponents,
			legacyRecord = record != null && record.PrefabOverrides == null,
			copiesInScene = isInstance ? Copies(entity).Count : 0
		};
	}
}
