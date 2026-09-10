using System;
using System.Linq;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.SceneComponentActions;
using static Voltage.Editor.Gateway.GatewayValues;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Scene-level components: the per-scene singletons the Scene Components pane manages.</summary>
internal static class SceneComponentCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("scenecomponent.types", "Scene component types that can be added.", (args, _) =>
		{
			var filter = args.String("filter");
			return ConcreteTypes(typeof(SceneComponent))
				.Where(t => string.IsNullOrEmpty(filter) || t.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
				.Select(t => new { name = t.Name, fullName = t.FullName, assembly = t.Assembly.GetName().Name })
				.OrderBy(t => t.fullName, StringComparer.Ordinal)
				.ToList();
		}, P.Str("filter", "Substring of the full type name")).ReadOnly();

		table.Add("scenecomponent.list", "Scene components on the open scene.", (_, _) =>
			All().Select(c => new { type = c.GetType().FullName, enabled = c.Enabled, serialized = c.IsSerialized }).ToList()).ReadOnly();

		table.Add("scenecomponent.add", "Add a scene component by type name (undoable); each type may exist once per scene.", (args, _) =>
		{
			var scene = RequireScene();
			var type = ResolveType(typeof(SceneComponent), args.Require("type"));
			if (All().Any(c => c.GetType() == type))
				throw new GatewayException($"the scene already has a {type.Name}");

			var instance = (SceneComponent)Activator.CreateInstance(type);
			instance.SetSerialized(true);

			// The component's own OnEnabled/OnStart run inside the add; a script bug there must not lose the undo entry.
			string warning = null;
			try
			{
				scene.AddSceneComponent(instance);
			}
			catch (Exception ex)
			{
				warning = $"{type.Name} threw while starting: {ex.GetType().Name}: {ex.Message}";
				Debug.Warn($"[Gateway] {warning}\n{ex.StackTrace}");
			}

			EditorChangeTracker.PushUndo(new SceneComponentAddedUndoAction(scene, instance), scene, $"Add SceneComponent {type.Name}");
			return new { component = Detail(instance), warning };
		}, TypeParam);

		table.Add("scenecomponent.remove", "Remove a scene component by type name (undoable).", (args, _) =>
		{
			var scene = RequireScene();
			var component = Find(args.Require("type"));
			var description = $"Remove SceneComponent {component.GetType().Name}";
			EditorChangeTracker.PushUndo(new SceneComponentRemovedUndoAction(scene, component, description), scene, description);
			scene.RemoveSceneComponent(component);
			return new { removed = true, type = component.GetType().FullName };
		}, TypeParam).Destructive();

		table.Add("scenecomponent.get", "Public fields and properties of a scene component.", (args, _) => Detail(Find(args.Require("type"))), TypeParam).ReadOnly();

		table.Add("scenecomponent.set", "Set a public field or property of a scene component (undoable).", (args, _) =>
		{
			var component = Find(args.Require("type"));
			var memberName = args.Require("member");
			var value = args.RequireProperty("value");
			var result = Set(component, memberName, value, undo: true, $"Set {component.GetType().Name}.{memberName}", args.Bool("remove"));
			return new { member = memberName, value = result };
		}, TypeParam, P.Str("member", "Field or property name; dotted paths reach nested members, Items[2] a list element, Items[+] appends", required: true), P.Any("value", "Value in the shared language: number, string, boolean, enum name, {x,y} vector, {r,g,b,a} or #RRGGBB colour, asset path or GUID for asset and prefab references, entity key for entity references, Entity/Type for component references, or a JSON array for a whole list", required: true), P.Bool("remove", "Remove the indexed list element instead of setting it", false));
	}

	private static readonly GatewayParam TypeParam = P.Str("type", "Scene component type name or full name", required: true);


	private static SceneComponent[] All()
	{
		var scene = RequireScene();
		var list = scene._sceneComponents;
		var result = new SceneComponent[list.Length];
		Array.Copy(list.Buffer, result, list.Length);
		return result;
	}

	private static SceneComponent Find(string typeName) =>
		All().FirstOrDefault(c => TypeMatches(c.GetType(), typeName))
		?? throw new GatewayException($"the scene has no scene component '{typeName}'; see scenecomponent.list");

	private static object Detail(SceneComponent c) => new
	{
		type = c.GetType().FullName,
		enabled = c.Enabled,
		serialized = c.IsSerialized,
		values = Snapshot(c)
	};
}
