using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.EntityActions;
using static Voltage.Editor.Gateway.GatewayValues;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Entity and component inspection and editing, routed through the undo system where the editor UI does the same.</summary>
internal static class EntityCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("entity.list", "Entities in the open scene.", (args, _) =>
		{
			var scene = RequireScene();
			var filter = args.String("filter");
			var component = args.String("component");
			return scene.Entities
				.Where(e => string.IsNullOrEmpty(filter) || (e.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
				.Where(e => string.IsNullOrEmpty(component) || e.GetComponents<Component>().Any(c => TypeMatches(c.GetType(), component)))
				.Select(Summary)
				.ToList();
		}, P.Str("filter", "Substring of the entity name"), P.Str("component", "Only entities carrying this component type")).ReadOnly();

		table.Add("entity.get", "Full detail of one entity.", (args, _) => Detail(ResolveEntity(args.Require("entity"))), P.Str("entity", "Entity id, GUID or name", required: true)).ReadOnly();

		table.Add("entity.create", "Create an empty entity.", (args, ctx) =>
		{
			var scene = RequireScene();
			var entity = new Entity("Entity", Entity.InstanceType.Serialized);
			entity.Name = scene.GetUniqueEntityName(args.String("name", "Entity"), entity);
			entity.Transform.Position = new Vector2(args.Float("x"), args.Float("y"));

			if (args.Has("parent"))
				entity.Transform.Parent = ResolveEntity(args.Require("parent")).Transform;

			scene.AddEntity(entity);
			var description = $"Create Entity {entity.Name}";
			EditorChangeTracker.PushUndo(new EntityCreateDeleteUndoAction(scene, entity, wasCreated: true, description), entity, description);
			Select(ctx, entity);
			return Detail(entity);
		}, P.Str("name", "Entity name; made unique in the scene", "Entity"), P.Float("x", "World X", 0f), P.Float("y", "World Y", 0f), P.Str("parent", "Parent entity id, GUID or name"));

		table.Add("entity.duplicate", "Clone an entity with its components and children (undoable).", (args, ctx) =>
		{
			var pane = RequirePane(ctx);
			var clone = pane.DuplicateEntity(ResolveEntity(args.Require("entity")), args.String("name"))
				?? throw new GatewayException("the entity could not be duplicated");
			return Detail(clone);
		}, P.Str("entity", "Entity id, GUID or name", required: true), P.Str("name", "Name for the clone; default derives from the original"));

		table.Add("entity.delete", "Destroy an entity and its children (undoable). Scene-required entities such as the main camera are refused.", (args, ctx) =>
		{
			var scene = RequireScene();
			var entity = ResolveEntity(args.Require("entity"));
			if (entity.Type == Entity.InstanceType.SceneRequired)
				throw new GatewayException($"{entity.Name} is scene-required and cannot be deleted");

			var description = $"Delete Entity {entity.Name}";
			ctx.ImGui().SceneGraphWindow?.EntityPane?.SelectedEntities.Remove(entity);

			// Same order as the scene graph's Destroy item: record while the entity is still intact, then destroy.
			EditorChangeTracker.PushUndo(new EntityCreateDeleteUndoAction(scene, entity, wasCreated: false, description), entity, description);
			entity.Destroy();
			return new { deleted = true, id = entity.Id };
		}, P.Str("entity", "Entity id, GUID or name", required: true)).Destructive();

		table.Add("entity.set", "Edit name, transform, enabled or parent. Only the position change is undoable.", (args, _) =>
		{
			var entity = ResolveEntity(args.Require("entity"));
			var changed = new List<string>();

			var x = args.OptFloat("x");
			var y = args.OptFloat("y");
			if (x.HasValue || y.HasValue)
			{
				var old = entity.Transform.Position;
				var next = new Vector2(x ?? old.X, y ?? old.Y);
				entity.Transform.Position = next;
				var description = $"Move {entity.Name}";
				EditorChangeTracker.PushUndo(new MultiEntityTransformUndoAction(
					new List<Entity> { entity },
					new Dictionary<Entity, Vector2> { [entity] = old },
					new Dictionary<Entity, Vector2> { [entity] = next },
					description), entity, description);
				changed.Add("position");
			}

			if (args.Has("rotation"))
			{
				entity.Transform.Rotation = MathHelper.ToRadians(args.Float("rotation"));
				Mark(entity, "rotation", changed);
			}

			var sx = args.OptFloat("scaleX");
			var sy = args.OptFloat("scaleY");
			if (sx.HasValue || sy.HasValue)
			{
				var old = entity.Transform.Scale;
				entity.Transform.Scale = new Vector2(sx ?? old.X, sy ?? old.Y);
				Mark(entity, "scale", changed);
			}

			if (args.Has("name"))
			{
				entity.Name = args.Require("name");
				Mark(entity, "name", changed);
			}

			if (args.Has("enabled"))
			{
				entity.Enabled = args.Bool("enabled", true);
				Mark(entity, "enabled", changed);
			}

			if (args.TryGet("parent", out var parent))
			{
				var key = parent.ValueKind == JsonValueKind.String ? parent.GetString() : parent.GetRawText();
				entity.Transform.Parent = string.IsNullOrEmpty(key) ? null : ResolveEntity(key).Transform;
				Mark(entity, "parent", changed);
			}

			return new { changed, entity = Detail(entity) };
		}, P.Str("entity", "Entity id, GUID or name", required: true), P.Str("name", "New name"), P.Float("x", "World X"), P.Float("y", "World Y"), P.Float("rotation", "Rotation in degrees"), P.Float("scaleX", "Scale X"), P.Float("scaleY", "Scale Y"), P.Bool("enabled", "Enable or disable the entity"), P.Str("parent", "Parent entity id, GUID or name, or \"\" to unparent"));

		table.Add("entity.reorder", "Move an entity to a sibling index or next to another entity, optionally under a new parent, as dragging in the scene graph does (undoable).", (args, _) =>
		{
			var scene = RequireScene();
			var entity = ResolveEntity(args.Require("entity"));
			if (entity.Type == Entity.InstanceType.SceneRequired)
				throw new GatewayException($"{entity.Name} is scene-required and stays at the root");

			Transform newParent = entity.Transform.Parent;
			if (args.TryGet("parent", out var parentElement))
			{
				var key = parentElement.ValueKind == JsonValueKind.String ? parentElement.GetString() : parentElement.GetRawText();
				newParent = string.IsNullOrEmpty(key) ? null : ResolveEntity(key).Transform;
			}
			for (var t = newParent; t != null; t = t.Parent)
				if (t == entity.Transform)
					throw new GatewayException("an entity cannot be parented under itself");

			var oldParent = entity.Transform.Parent;
			var oldIndex = oldParent != null ? oldParent.Children.IndexOf(entity.Transform) : scene.Entities.EntityFastList.IndexOf(entity);

			int newIndex;
			if (args.Has("index"))
				newIndex = args.Int("index");
			else if (args.Has("before") || args.Has("after"))
			{
				var anchor = ResolveEntity(args.Require(args.Has("before") ? "before" : "after"));
				if (anchor == entity)
					throw new GatewayException("before/after cannot be the entity itself");
				newParent = anchor.Transform.Parent;
				newIndex = SiblingIndex(scene, anchor) + (args.Has("after") ? 1 : 0);
			}
			else
				throw new GatewayException("pass 'index', 'before' or 'after'");

			if (newParent == oldParent && oldIndex >= 0 && oldIndex < newIndex)
				newIndex--;
			var count = newParent != null ? newParent.Children.Count : scene.Entities.Count;
			newIndex = Math.Clamp(newIndex, 0, Math.Max(0, newParent == oldParent ? count - 1 : count));

			var worldPos = entity.Transform.Position;
			var worldRot = entity.Transform.Rotation;
			var worldScale = entity.Transform.Scale;
			entity.Transform.SetParentAt(newParent, newIndex);
			if (newParent == null)
				EntityReparentUndoAction.MoveRootTo(entity, newIndex);
			entity.Transform.RecomputeLocalsFromWorld(worldPos, worldRot, worldScale);

			var entries = new List<(Entity entity, Transform oldParent, int oldIndex, Transform newParent, int newIndex)> { (entity, oldParent, oldIndex, newParent, newIndex) };
			var description = $"Reorder {entity.Name}";
			EditorChangeTracker.PushUndo(new EntityReparentUndoAction(entries, description), entity, description);
			return new { index = SiblingIndex(scene, entity), parent = newParent?.Entity?.Id, entity = Detail(entity) };
		}, P.Str("entity", "Entity id, GUID or name", required: true), P.Int("index", "Target sibling index under the parent"), P.Str("before", "Place just before this sibling"), P.Str("after", "Place just after this sibling"), P.Str("parent", "New parent entity, or \"\" for the scene root; default keeps the current parent"));

		table.Add("entity.select", "Select entities in the scene graph.", (args, ctx) =>
		{
			var pane = RequirePane(ctx);
			var keys = args.Strings("entities");
			if (keys.Count == 0)
				keys = args.Strings("entity");
			if (keys.Count == 0)
				throw new GatewayException("pass 'entity' or 'entities'");

			if (!args.Bool("add"))
				pane.DeselectAllEntities();

			foreach (var key in keys)
				pane.SetSelectedEntity(ResolveEntity(key), ctrlDown: true);

			ctx.ImGui().MainEntityInspectorWindow?.DelayedSetEntity(pane.SelectedEntities.LastOrDefault());
			return pane.SelectedEntities.Select(Summary).ToList();
		}, P.List("entities", "Entity ids, GUIDs or names to select"), P.Str("entity", "A single entity id, GUID or name"), P.Bool("add", "Keep the current selection", false));

		table.Add("entity.deselect", "Clear the scene-graph selection.", (_, ctx) =>
		{
			RequirePane(ctx).DeselectAllEntities();
			return new { selected = 0 };
		});

		table.Add("entity.selected", "The current scene-graph selection.", (_, ctx) => RequirePane(ctx).SelectedEntities.Select(Summary).ToList()).ReadOnly();

		table.Add("component.types", "Component types that can be added.", (args, _) =>
		{
			var filter = args.String("filter");
			return ConcreteTypes(typeof(Component))
				.Where(t => string.IsNullOrEmpty(filter) || t.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
				.Select(t => new { name = t.Name, fullName = t.FullName, assembly = t.Assembly.GetName().Name })
				.OrderBy(t => t.fullName, StringComparer.Ordinal)
				.ToList();
		}, P.Str("filter", "Substring of the full type name")).ReadOnly();

		table.Add("component.add", "Add a component by type name (undoable).", (args, _) =>
		{
			var entity = ResolveEntity(args.Require("entity"));
			var type = ResolveType(typeof(Component), args.Require("type"));
			if (Activator.CreateInstance(type) is not Component component)
				throw new GatewayException($"{type.FullName} has no parameterless constructor");

			component.SetSerialized(true);
			entity.AddComponent(component);
			var action = new ComponentAddedUndoAction(entity, component);
			EditorChangeTracker.PushUndo(action, entity, action.Description);
			return ComponentDetail(component);
		}, P.Str("entity", "Entity id, GUID or name", required: true), P.Str("type", "Component type name or full name", required: true));

		table.Add("component.remove", "Remove a component by type name.", (args, _) =>
		{
			var component = FindComponent(args);
			var entity = component.Entity;
			component.RemoveComponent();
			EditorChangeTracker.MarkChanged(entity, $"Remove {component.GetType().Name} from {entity.Name}");
			return new { removed = true, type = component.GetType().FullName };
		}, P.Str("entity", "Entity id, GUID or name", required: true), P.Str("type", "Component type name or full name", required: true)).Destructive();

		table.Add("component.get", "Public fields and properties of a component.", (args, _) => ComponentDetail(FindComponent(args)), P.Str("entity", "Entity id, GUID or name", required: true), P.Str("type", "Component type name or full name", required: true)).ReadOnly();

		table.Add("component.set", "Set a public field or property (undoable). Values: numbers, strings, enums by name, {x,y}, {r,g,b,a}, an asset path for asset and prefab references, an entity key for entity references, 'Entity/Type' for component references.", (args, _) =>
		{
			var component = FindComponent(args);
			var memberName = args.Require("member");
			var value = args.RequireProperty("value");

			var result = Set(component, memberName, value, undo: true, $"Set {component.GetType().Name}.{memberName} on {component.Entity?.Name}");
			return new { member = memberName, value = result };
		}, P.Str("entity", "Entity id, GUID or name", required: true), P.Str("type", "Component type name or full name", required: true), P.Str("member", "Field or property name; dotted paths reach nested members", required: true), P.Any("value", "New value in the shared value language: number, string, bool, enum name, {x,y}, {r,g,b,a}, asset path, entity key or Entity/Type", required: true));
	}


	private static int SiblingIndex(Scene scene, Entity entity) =>
		entity.Transform.Parent != null ? entity.Transform.Parent.Children.IndexOf(entity.Transform) : scene.Entities.EntityFastList.IndexOf(entity);

	private static Inspectors.SceneGraphPanes.EntityPane RequirePane(GatewayContext ctx) =>
		ctx.ImGui().SceneGraphWindow?.EntityPane ?? throw new GatewayException("scene graph window is not available");

	private static void Select(GatewayContext ctx, Entity entity)
	{
		var pane = ctx.ImGui().SceneGraphWindow?.EntityPane;
		if (pane == null)
			return;
		pane.SetSelectedEntity(entity, ctrlDown: false);
		ctx.ImGui().MainEntityInspectorWindow?.DelayedSetEntity(entity);
	}

	private static void Mark(Entity entity, string what, List<string> changed)
	{
		EditorChangeTracker.MarkChanged(entity, $"Set {what} of {entity.Name}");
		changed.Add(what);
	}

	private static Component FindComponent(GatewayArgs args) => ResolveComponent(ResolveEntity(args.Require("entity")), args.Require("type"));

	public static object Summary(Entity e) => new
	{
		id = e.Id,
		guid = e.PersistentId,
		name = e.Name,
		enabled = e.Enabled,
		parent = e.Transform.Parent?.Entity?.Id,
		x = e.Transform.Position.X,
		y = e.Transform.Position.Y
	};

	public static object Detail(Entity e) => new
	{
		id = e.Id,
		guid = e.PersistentId,
		name = e.Name,
		enabled = e.Enabled,
		tag = e.Tag,
		parent = e.Transform.Parent?.Entity?.Id,
		children = e.Transform.Children.Select(c => c.Entity.Id).ToList(),
		position = new { x = e.Transform.Position.X, y = e.Transform.Position.Y },
		localPosition = new { x = e.Transform.LocalPosition.X, y = e.Transform.LocalPosition.Y },
		rotation = MathHelper.ToDegrees(e.Transform.Rotation),
		scale = new { x = e.Transform.Scale.X, y = e.Transform.Scale.Y },
		components = e.GetComponents<Component>().Select(c => new { type = c.GetType().FullName, name = c.Name, enabled = c.Enabled }).ToList()
	};

	private static object ComponentDetail(Component c) => new
	{
		type = c.GetType().FullName,
		name = c.Name,
		enabled = c.Enabled,
		entity = c.Entity?.Id,
		values = Snapshot(c)
	};
}
