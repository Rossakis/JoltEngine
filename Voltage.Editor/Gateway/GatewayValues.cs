using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Voltage.Editor.Assets;
using Voltage.Editor.Undo.Core;
using AssetReference = Voltage.Serialization.AssetReference;
using ComponentReference = Voltage.Serialization.ComponentReference;
using EntityReference = Voltage.Serialization.EntityReference;
using PrefabReference = Voltage.Serialization.PrefabReference;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway;

/// <summary>Reflection over the members an agent may read and write, plus the JSON conversions for engine value and reference types.</summary>
internal static class GatewayValues
{
	private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal) { "Entity", "Transform", "Scene" };

	public static Scene RequireScene() => Core.Scene ?? throw new GatewayException("no scene loaded");

	public static ProjectFile.IGameProject RequireProject() => ProjectFile.ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");

	public static AssetDatabase RequireAssets() => AssetDatabase.Instance ?? throw new GatewayException("no project loaded; the asset database is empty");

	/// <summary>Public instance fields and publicly settable properties the gateway can write directly.</summary>
	public static IEnumerable<MemberInfo> Members(Type type) => AllMembers(type).Where(m => IsSupported(MemberType(m)));

	/// <summary>Writable leaves plus the nested objects that hold more of them.</summary>
	private static IEnumerable<MemberInfo> AllMembers(Type type)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
		foreach (var f in type.GetFields(flags))
			if (!f.IsInitOnly && !Hidden.Contains(f.Name) && (IsSupported(f.FieldType) || IsNested(f.FieldType)))
				yield return f;
		foreach (var p in type.GetProperties(flags))
		{
			if (!p.CanRead || p.GetIndexParameters().Length > 0 || Hidden.Contains(p.Name) || p.GetGetMethod() == null)
				continue;
			var writable = p.GetSetMethod() != null;
			if ((writable && IsSupported(p.PropertyType)) || (IsNested(p.PropertyType) && (writable || p.PropertyType.IsClass)))
				yield return p;
		}
	}

	public static MemberInfo FindMember(Type type, string name) =>
		Members(type).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
		?? throw new GatewayException($"no public writable member '{name}' on {type.Name}; see the 'values' of a get call for what exists");

	private static MemberInfo FindAnyMember(Type type, string name) =>
		AllMembers(type).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
		?? throw new GatewayException($"no public member '{name}' on {type.Name}");

	/// <summary>A plain data object or struct whose own members are worth showing; engine objects and collections are not walked.</summary>
	private static bool IsNested(Type t)
	{
		t = Nullable.GetUnderlyingType(t) ?? t;
		if (IsSupported(t) || t == typeof(object) || t.IsArray || t.IsPointer || typeof(Delegate).IsAssignableFrom(t))
			return false;
		if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t) || typeof(Component).IsAssignableFrom(t) || typeof(SceneComponent).IsAssignableFrom(t))
			return false;
		var ns = t.Namespace ?? "";
		if (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft.Xna", StringComparison.Ordinal))
			return false;
		return t.IsClass || (t.IsValueType && !t.IsEnum);
	}

	public static Type MemberType(MemberInfo m) => m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;

	public static object Get(object target, MemberInfo m) => m is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)m).GetValue(target);

	private static void Put(object target, MemberInfo m, object value)
	{
		if (m is FieldInfo f)
			f.SetValue(target, value);
		else
			((PropertyInfo)m).SetValue(target, value);
	}

	public static bool IsSupported(Type t)
	{
		t = Nullable.GetUnderlyingType(t) ?? t;
		return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
			|| t == typeof(Vector2) || t == typeof(Color) || t == typeof(Rectangle) || t == typeof(RectangleF)
			|| t == typeof(AssetReference) || t == typeof(EntityReference) || t == typeof(ComponentReference) || t == typeof(PrefabReference)
			|| t == typeof(Entity) || t == typeof(Transform) || typeof(Component).IsAssignableFrom(t);
	}

	/// <summary>Every member of the target, nested data objects included, described for JSON.</summary>
	public static Dictionary<string, object> Snapshot(object target, int depth = 0)
	{
		var values = new Dictionary<string, object>();
		foreach (var member in AllMembers(target.GetType()))
		{
			try
			{
				var value = Get(target, member);
				values[member.Name] = value != null && IsNested(value.GetType())
					? (depth < 3 ? Snapshot(value, depth + 1) : value.ToString())
					: Describe(value);
			}
			catch (Exception) { }
		}
		return values;
	}

	/// <summary>Converts and writes one member, addressed by a dotted path through nested objects. With undo, the change is recorded against the target like an inspector edit.</summary>
	public static object Set(object target, string memberPath, JsonElement value, bool undo, string description)
	{
		var segments = memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (segments.Length == 0)
			throw new GatewayException("missing member name");

		var holders = new List<object> { target };
		var chain = new List<MemberInfo>();
		for (var i = 0; i < segments.Length - 1; i++)
		{
			var holder = holders[^1];
			var member = FindAnyMember(holder.GetType(), segments[i]);
			var child = Get(holder, member);
			if (child == null)
			{
				var childType = MemberType(member);
				child = childType.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(childType) : null;
				if (child == null)
					throw new GatewayException($"{member.Name} is null and cannot be created");
			}
			holders.Add(child);
			chain.Add(member);
		}

		var leafHolder = holders[^1];
		var leaf = FindMember(leafHolder.GetType(), segments[^1]);
		var converted = Convert(value, MemberType(leaf));
		var old = Get(leafHolder, leaf);

		Put(leafHolder, leaf, converted);

		// Reference-typed intermediates were mutated in place; only boxed structs need writing back.
		for (var i = chain.Count - 1; i >= 0; i--)
			if (holders[i + 1].GetType().IsValueType)
				Put(holders[i], chain[i], holders[i + 1]);

		if (undo && !Equals(old, converted))
		{
			var path = chain.Select(m => m.Name).Append(leaf.Name).ToList();
			EditorChangeTracker.PushUndo(new PathUndoAction(target, path, old, converted, description), target, description);
		}

		return Describe(converted);
	}

	public static object Describe(object value) => value switch
	{
		null => null,
		Vector2 v => new { x = v.X, y = v.Y },
		Color c => new { r = c.R, g = c.G, b = c.B, a = c.A },
		Rectangle r => new { r.X, r.Y, r.Width, r.Height },
		RectangleF r => new { r.X, r.Y, r.Width, r.Height },
		Enum e => e.ToString(),
		AssetReference a => a.IsValid ? new { guid = a.AssetGuid, path = a.AssetPath, name = a.AssetName } : null,
		EntityReference e => e.IsValid ? new { persistentId = e.EntityPersistentId, name = e.EntityName } : null,
		ComponentReference c => c.IsValid ? new { entity = c.EntityName, persistentId = c.EntityPersistentId, type = c.ComponentTypeName, name = c.ComponentName } : null,
		PrefabReference p => p.IsValid ? new { guid = p.PrefabGuid, path = p.PrefabPath, name = p.PrefabName } : null,
		Entity e => new { id = e.Id, guid = e.PersistentId, name = e.Name },
		Transform t => t.Entity == null ? null : new { id = t.Entity.Id, guid = t.Entity.PersistentId, name = t.Entity.Name },
		Component c => new { entity = c.Entity?.Name, entityId = c.Entity?.Id, type = c.GetType().FullName, name = c.Name },
		_ => value
	};

	/// <summary>JSON to a member's type. References accept the same keys the entity and asset commands do.</summary>
	public static object Convert(JsonElement value, Type target)
	{
		var t = Nullable.GetUnderlyingType(target) ?? target;
		var isNull = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (value.ValueKind == JsonValueKind.String && value.GetString() == "");
		if (isNull && Nullable.GetUnderlyingType(target) != null)
			return null;

		if (t == typeof(AssetReference))
		{
			if (isNull) return default(AssetReference);
			var item = ResolveAsset(Text(value));
			var reference = AssetDatabase.Instance.GetReference(item.AbsolutePath);
			return new AssetReference { AssetGuid = reference.Guid, AssetPath = reference.HintPath, AssetName = Path.GetFileNameWithoutExtension(item.AbsolutePath) };
		}
		if (t == typeof(PrefabReference))
		{
			if (isNull) return default(PrefabReference);
			var item = ResolveAsset(Text(value));
			var reference = AssetDatabase.Instance.GetReference(item.AbsolutePath);
			return new PrefabReference { PrefabGuid = reference.Guid, PrefabPath = reference.HintPath, PrefabName = Path.GetFileNameWithoutExtension(item.AbsolutePath) };
		}
		if (t == typeof(EntityReference))
			return isNull ? default(EntityReference) : EntityReference.From(ResolveEntity(Text(value)));
		if (t == typeof(Entity))
			return isNull ? null : ResolveEntity(Text(value));
		if (t == typeof(Transform))
			return isNull ? null : ResolveEntity(Text(value)).Transform;
		if (t == typeof(ComponentReference))
			return isNull ? default(ComponentReference) : ComponentReference.From(ResolveComponentValue(value, typeof(Component)));
		if (typeof(Component).IsAssignableFrom(t))
			return isNull ? null : ResolveComponentValue(value, t);

		if (t == typeof(Vector2))
			return new Vector2(Num(value, "x", 0), Num(value, "y", 1));
		if (t == typeof(Color))
			return new Color((int)Num(value, "r", 0), (int)Num(value, "g", 1), (int)Num(value, "b", 2), (int)Num(value, "a", 3, 255));
		if (t == typeof(Rectangle))
			return new Rectangle((int)Num(value, "x", 0), (int)Num(value, "y", 1), (int)Num(value, "width", 2), (int)Num(value, "height", 3));
		if (t == typeof(RectangleF))
			return new RectangleF(Num(value, "x", 0), Num(value, "y", 1), Num(value, "width", 2), Num(value, "height", 3));
		if (t.IsEnum)
			return Enum.Parse(t, Text(value), true);
		if (t == typeof(string))
			return value.ValueKind == JsonValueKind.Null ? null : Text(value);
		if (t == typeof(bool))
			return value.ValueKind == JsonValueKind.String ? bool.Parse(value.GetString()) : value.GetBoolean();

		return System.Convert.ChangeType(Text(value), t, System.Globalization.CultureInfo.InvariantCulture);
	}

	private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

	/// <summary>A numeric part of an object by key, or of an array by index.</summary>
	private static float Num(JsonElement value, string key, int index, float fallback = 0)
	{
		if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
			return prop.GetSingle();
		if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > index)
			return value[index].GetSingle();
		return fallback;
	}

	/// <summary>"Player/SpriteRenderer", "Player:SpriteRenderer" or {entity, type}.</summary>
	private static Component ResolveComponentValue(JsonElement value, Type required)
	{
		string entityKey, typeName;
		if (value.ValueKind == JsonValueKind.Object)
		{
			entityKey = value.TryGetProperty("entity", out var e) ? Text(e) : null;
			typeName = value.TryGetProperty("type", out var ty) ? Text(ty) : null;
		}
		else
		{
			var text = Text(value);
			var split = text.LastIndexOfAny(new[] { '/', ':' });
			entityKey = split > 0 ? text.Substring(0, split) : text;
			typeName = split > 0 ? text.Substring(split + 1) : null;
		}

		if (string.IsNullOrEmpty(entityKey))
			throw new GatewayException("a component value needs an entity, as 'Entity/Type' or {entity, type}");

		var entity = ResolveEntity(entityKey);
		var candidates = entity.GetComponents<Component>().Where(c => required.IsAssignableFrom(c.GetType())).ToList();
		var component = string.IsNullOrEmpty(typeName)
			? candidates.FirstOrDefault()
			: candidates.FirstOrDefault(c => TypeMatches(c.GetType(), typeName));
		return component ?? throw new GatewayException($"{entity.Name} has no {(string.IsNullOrEmpty(typeName) ? required.Name : typeName)} component");
	}

	public static bool TypeMatches(Type type, string name) =>
		type.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || (type.FullName ?? "").Equals(name, StringComparison.OrdinalIgnoreCase);

	/// <summary>Accepts a numeric id, a persistent GUID or an entity name.</summary>
	public static Entity ResolveEntity(string key)
	{
		var scene = Core.Scene ?? throw new GatewayException("no scene loaded");

		if (uint.TryParse(key, out var id))
		{
			var byId = scene.Entities.FirstOrDefault(e => e.Id == id);
			if (byId != null)
				return byId;
		}

		if (Guid.TryParse(key, out var guid))
		{
			var byGuid = scene.Entities.FindEntityByPersistentId(guid);
			if (byGuid != null)
				return byGuid;
		}

		return scene.Entities.FindEntity(key) ?? throw new GatewayException($"entity not found: {key}");
	}

	public static Component ResolveComponent(Entity entity, string typeName) =>
		entity.GetComponents<Component>().FirstOrDefault(c => TypeMatches(c.GetType(), typeName))
		?? throw new GatewayException($"{entity.Name} has no component '{typeName}'");

	/// <summary>Accepts a GUID, an absolute path, a project-relative path, or a file name.</summary>
	public static AssetItem ResolveAsset(string key)
	{
		var db = AssetDatabase.Instance ?? throw new GatewayException("no project loaded; the asset database is empty");

		if (Guid.TryParse(key, out var guid))
		{
			var path = db.GetPath(guid);
			var byGuid = path != null ? db.Items.FirstOrDefault(i => PathsEqual(i.AbsolutePath, path)) : null;
			if (byGuid != null)
				return byGuid;
		}

		var full = Path.IsPathRooted(key) ? Path.GetFullPath(key) : null;
		var normalized = key.Replace('\\', '/');
		return db.Items.FirstOrDefault(i => full != null && PathsEqual(i.AbsolutePath, full))
			?? db.Items.FirstOrDefault(i => i.AbsolutePath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
			?? db.Items.FirstOrDefault(i => i.FileName.Equals(key, StringComparison.OrdinalIgnoreCase))
			?? db.Items.FirstOrDefault(i => Path.GetFileNameWithoutExtension(i.FileName).Equals(key, StringComparison.OrdinalIgnoreCase))
			?? throw new GatewayException($"asset not found: {key}");
	}

	private static bool PathsEqual(string a, string b) =>
		string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

	/// <summary>Concrete, constructible types assignable to the base, skipping script assemblies a recompile has replaced.</summary>
	public static IEnumerable<Type> ConcreteTypes(Type baseType)
	{
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly.IsDynamic)
				continue;
			var name = assembly.GetName().Name ?? "";
			if (name.StartsWith("DynamicScripts", StringComparison.Ordinal) && assembly != Core.LatestScriptAssembly)
				continue;

			Type[] types;
			try { types = assembly.GetTypes(); }
			catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

			foreach (var type in types)
				if (baseType.IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface && type.IsPublic && type.GetConstructor(Type.EmptyTypes) != null)
					yield return type;
		}
	}

	/// <summary>Matches by full or short name; the newest script assembly wins ties left by recompiles.</summary>
	public static Type ResolveType(Type baseType, string name)
	{
		var matches = ConcreteTypes(baseType).Where(t => TypeMatches(t, name)).ToList();
		if (matches.Count == 0)
			throw new GatewayException($"unknown {baseType.Name} type '{name}'");
		return matches.FirstOrDefault(t => t.Assembly == Core.LatestScriptAssembly) ?? matches[0];
	}
}
