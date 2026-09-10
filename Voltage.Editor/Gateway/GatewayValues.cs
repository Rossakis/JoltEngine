using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Voltage.Editor.Assets;
using Voltage.Editor.Inspectors.TypeInspectors;
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

	private static readonly Regex SegmentPattern = new(@"^([A-Za-z_]\w*)(?:\[(\+|\d*)\])?$", RegexOptions.Compiled);

	private readonly record struct Segment(string Name, string Index);

	private readonly record struct Step(MemberInfo Member, int Index);

	public static Scene RequireScene() => Core.Scene ?? throw new GatewayException("no scene loaded");

	public static ProjectFile.IGameProject RequireProject() => ProjectFile.ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");

	public static AssetDatabase RequireAssets() => AssetDatabase.Instance ?? throw new GatewayException("no project loaded; the asset database is empty");

	/// <summary>Public instance fields and publicly settable properties the gateway can write directly, lists included.</summary>
	public static IEnumerable<MemberInfo> Members(Type type) => AllMembers(type).Where(m => IsSupported(MemberType(m)) || IsList(MemberType(m)));

	/// <summary>Writable leaves, lists, plus the nested objects that hold more of them.</summary>
	private static IEnumerable<MemberInfo> AllMembers(Type type)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
		// Only engine components carry the owner back-references worth hiding; a data object may legitimately have an Entity field.
		var hidden = typeof(Component).IsAssignableFrom(type) || typeof(SceneComponent).IsAssignableFrom(type) ? Hidden : null;
		foreach (var f in type.GetFields(flags))
			if (!f.IsInitOnly && hidden?.Contains(f.Name) != true && (IsSupported(f.FieldType) || IsNested(f.FieldType) || IsList(f.FieldType)))
				yield return f;
		foreach (var p in type.GetProperties(flags))
		{
			if (!p.CanRead || p.GetIndexParameters().Length > 0 || hidden?.Contains(p.Name) == true || p.GetGetMethod() == null)
				continue;
			var writable = p.GetSetMethod() != null;
			if ((writable && IsSupported(p.PropertyType)) || (IsNested(p.PropertyType) && (writable || p.PropertyType.IsClass)) || (IsList(p.PropertyType) && (writable || !p.PropertyType.IsArray)))
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
		if (typeof(IEnumerable).IsAssignableFrom(t) || typeof(Component).IsAssignableFrom(t) || typeof(SceneComponent).IsAssignableFrom(t))
			return false;
		var ns = t.Namespace ?? "";
		if (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft.Xna", StringComparison.Ordinal))
			return false;
		return t.IsClass || (t.IsValueType && !t.IsEnum);
	}

	/// <summary>A List or array whose elements the gateway can convert.</summary>
	public static bool IsList(Type t)
	{
		var element = ElementType(t);
		return element != null && (IsSupported(element) || IsNested(element));
	}

	private static Type ElementType(Type t)
	{
		if (t.IsArray)
			return t.GetArrayRank() == 1 ? t.GetElementType() : null;
		return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null;
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
		return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(TimeSpan)
			|| t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Point) || t == typeof(Color) || t == typeof(Rectangle) || t == typeof(RectangleF)
			|| t == typeof(AssetReference) || t == typeof(EntityReference) || t == typeof(ComponentReference) || t == typeof(PrefabReference)
			|| t == typeof(Entity) || t == typeof(Transform) || typeof(Component).IsAssignableFrom(t);
	}

	/// <summary>Every member of the target, nested data objects and lists included, described for JSON.</summary>
	public static Dictionary<string, object> Snapshot(object target, int depth = 0)
	{
		var values = new Dictionary<string, object>();
		foreach (var member in AllMembers(target.GetType()))
		{
			try
			{
				values[member.Name] = DescribeDeep(Get(target, member), depth);
			}
			catch (Exception) { }
		}
		return values;
	}

	private static object DescribeDeep(object value, int depth)
	{
		if (value is IList list && value is not string)
			return list.Cast<object>().Select(e => DescribeDeep(e, depth + 1)).ToList();
		if (value != null && IsNested(value.GetType()))
			return depth < 3 ? Snapshot(value, depth + 1) : value.ToString();
		return Describe(value);
	}

	private static List<Segment> ParsePath(string memberPath)
	{
		var segments = new List<Segment>();
		foreach (var part in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var match = SegmentPattern.Match(part);
			if (!match.Success)
				throw new GatewayException($"bad member path segment '{part}'; use Name, Name[2] or Name[+]");
			segments.Add(new Segment(match.Groups[1].Value, match.Groups[2].Success ? (match.Groups[2].Value == "" ? "+" : match.Groups[2].Value) : null));
		}
		if (segments.Count == 0)
			throw new GatewayException("missing member name");
		return segments;
	}

	private static int ListIndex(string index, int count, string name)
	{
		if (index == "+" || !int.TryParse(index, out var i))
			throw new GatewayException($"{name} needs a numeric index here");
		if (i < 0 || i >= count)
			throw new GatewayException($"{name}[{i}] is out of range (0..{count - 1})");
		return i;
	}

	/// <summary>Converts and writes one member, addressed by a dotted path with optional list indices (Clips[2], Clips[+]); with remove, the indexed element is dropped. With undo, the change is recorded against the target like an inspector edit.</summary>
	public static object Set(object target, string memberPath, JsonElement value, bool undo, string description, bool remove = false)
	{
		var segments = ParsePath(memberPath);
		var holders = new List<object> { target };
		var steps = new List<Step>();
		var path = new List<string>();

		for (var i = 0; i < segments.Count - 1; i++)
		{
			var segment = segments[i];
			var holder = holders[^1];
			var member = FindAnyMember(holder.GetType(), segment.Name);
			var child = Get(holder, member) ?? CreateChild(member);
			holders.Add(child);
			steps.Add(new Step(member, -1));
			path.Add(member.Name);

			if (segment.Index == null)
				continue;
			var list = child as IList ?? throw new GatewayException($"{member.Name} is not a list");
			var index = ListIndex(segment.Index, list.Count, member.Name);
			var element = list[index];
			if (element == null)
			{
				var elementType = ElementType(MemberType(member));
				element = elementType?.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(elementType) : throw new GatewayException($"{member.Name}[{index}] is null");
				list[index] = element;
			}
			holders.Add(element);
			steps.Add(new Step(null, index));
			path.Add($"[{index}]");
		}

		var last = segments[^1];
		var leafHolder = holders[^1];
		object result;

		if (last.Index == null)
		{
			var leaf = FindMember(leafHolder.GetType(), last.Name);
			var leafType = MemberType(leaf);
			if (IsList(leafType))
			{
				if (remove)
					throw new GatewayException($"pass an index to remove from {leaf.Name}, e.g. {leaf.Name}[0]");
				var old = CloneList(Get(leafHolder, leaf) as IList);
				var created = ConvertList(value, leafType, leaf);
				Put(leafHolder, leaf, created);
				WriteBack(holders, steps);
				if (undo)
					PushUndo(target, path, leaf.Name, old, CloneList(created), description);
				result = DescribeDeep(created, 0);
			}
			else
			{
				var converted = Convert(value, leafType, leaf);
				var old = Get(leafHolder, leaf);
				Put(leafHolder, leaf, converted);
				WriteBack(holders, steps);
				if (undo && !Equals(old, converted))
					PushUndo(target, path, leaf.Name, old, converted, description);
				result = Describe(converted);
			}
			return result;
		}

		var listMember = FindAnyMember(leafHolder.GetType(), last.Name);
		var listType = MemberType(listMember);
		if (!IsList(listType))
			throw new GatewayException($"{listMember.Name} is not a list");
		var elementKind = ElementType(listType);
		var current = Get(leafHolder, listMember) as IList ?? NewList(listType, 0);
		var before = CloneList(current);
		IList after;

		if (remove)
		{
			var index = ListIndex(last.Index, current.Count, listMember.Name);
			after = WithoutAt(current, index, listType);
			result = new { removed = index, count = after.Count };
		}
		else if (last.Index == "+")
		{
			var converted = ConvertElement(value, elementKind, listMember);
			after = Appended(current, converted, listType);
			result = DescribeDeep(converted, 0);
		}
		else
		{
			var index = ListIndex(last.Index, current.Count, listMember.Name);
			var converted = ConvertElement(value, elementKind, listMember);
			after = current;
			after[index] = converted;
			result = DescribeDeep(converted, 0);
		}

		Put(leafHolder, listMember, after);
		WriteBack(holders, steps);
		if (undo)
			PushUndo(target, path, listMember.Name, before, CloneList(after), description);
		return result;
	}

	private static object CreateChild(MemberInfo member)
	{
		var childType = MemberType(member);
		if (childType.GetConstructor(Type.EmptyTypes) == null && !childType.IsValueType)
			throw new GatewayException($"{member.Name} is null and cannot be created");
		return Activator.CreateInstance(childType);
	}

	/// <summary>Reference-typed intermediates were mutated in place; only boxed structs need writing back, into a member or a list slot.</summary>
	private static void WriteBack(List<object> holders, List<Step> steps)
	{
		for (var i = steps.Count - 1; i >= 0; i--)
		{
			if (!holders[i + 1].GetType().IsValueType)
				continue;
			if (steps[i].Member != null)
				Put(holders[i], steps[i].Member, holders[i + 1]);
			else
				((IList)holders[i])[steps[i].Index] = holders[i + 1];
		}
	}

	private static void PushUndo(object target, List<string> path, string leaf, object old, object created, string description)
	{
		var full = new List<string>(path) { leaf };
		EditorChangeTracker.PushUndo(new PathUndoAction(target, full, old, created, description), target, description);
	}

	private static IList NewList(Type listType, int length) =>
		listType.IsArray ? Array.CreateInstance(listType.GetElementType(), length) : (IList)Activator.CreateInstance(listType);

	private static IList CloneList(IList list)
	{
		if (list == null)
			return null;
		if (list is Array array)
			return (Array)array.Clone();
		var clone = (IList)Activator.CreateInstance(list.GetType());
		foreach (var item in list)
			clone.Add(item);
		return clone;
	}

	private static IList Appended(IList list, object item, Type listType)
	{
		if (!listType.IsArray)
		{
			list.Add(item);
			return list;
		}
		var array = NewList(listType, list.Count + 1);
		for (var i = 0; i < list.Count; i++)
			array[i] = list[i];
		array[list.Count] = item;
		return array;
	}

	private static IList WithoutAt(IList list, int index, Type listType)
	{
		if (!listType.IsArray)
		{
			list.RemoveAt(index);
			return list;
		}
		var array = NewList(listType, list.Count - 1);
		for (int i = 0, j = 0; i < list.Count; i++)
			if (i != index)
				array[j++] = list[i];
		return array;
	}

	private static IList ConvertList(JsonElement value, Type listType, MemberInfo owner)
	{
		if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			return NewList(listType, 0);
		if (value.ValueKind != JsonValueKind.Array)
			throw new GatewayException($"{owner.Name} takes a JSON array, or an indexed path such as {owner.Name}[+] for one element");
		var elementType = ElementType(listType);
		var items = value.EnumerateArray().Select(e => ConvertElement(e, elementType, owner)).ToList();
		var list = NewList(listType, items.Count);
		for (var i = 0; i < items.Count; i++)
			if (listType.IsArray) list[i] = items[i];
			else list.Add(items[i]);
		return list;
	}

	/// <summary>One list element: a supported value, or a JSON object filled into a fresh nested data object.</summary>
	private static object ConvertElement(JsonElement value, Type elementType, MemberInfo owner)
	{
		if (IsSupported(elementType))
			return Convert(value, elementType, owner);
		if (!IsNested(elementType))
			throw new GatewayException($"elements of {owner.Name} ({elementType.Name}) cannot be set over the gateway");
		if (value.ValueKind != JsonValueKind.Object)
			throw new GatewayException($"an element of {owner.Name} is a {elementType.Name} object; pass its fields as a JSON object");
		var element = Activator.CreateInstance(elementType);
		foreach (var property in value.EnumerateObject())
			Set(element, property.Name, property.Value, false, null);
		return element;
	}

	public static object Describe(object value) => value switch
	{
		null => null,
		Vector2 v => new { x = v.X, y = v.Y },
		Vector3 v => new { x = v.X, y = v.Y, z = v.Z },
		Point p => new { x = p.X, y = p.Y },
		Color c => new { r = c.R, g = c.G, b = c.B, a = c.A },
		Rectangle r => new { r.X, r.Y, r.Width, r.Height },
		RectangleF r => new { r.X, r.Y, r.Width, r.Height },
		TimeSpan t => t.TotalSeconds,
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

	/// <summary>JSON to a member's type. References accept the same keys the entity and asset commands do; an [AssetType] on the member is enforced.</summary>
	public static object Convert(JsonElement value, Type target, MemberInfo member = null)
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
			CheckAssetType(member, item, reference.Guid);
			return new AssetReference { AssetGuid = reference.Guid, AssetPath = reference.HintPath, AssetName = Path.GetFileNameWithoutExtension(item.AbsolutePath) };
		}
		if (t == typeof(PrefabReference))
		{
			if (isNull) return default(PrefabReference);
			var item = ResolveAsset(Text(value));
			if (!item.Extension.Equals(".vprefab", StringComparison.OrdinalIgnoreCase))
				throw new GatewayException($"{item.FileName} is not a .vprefab");
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
		if (t == typeof(Vector3))
			return new Vector3(Num(value, "x", 0), Num(value, "y", 1), Num(value, "z", 2));
		if (t == typeof(Point))
			return new Point((int)Num(value, "x", 0), (int)Num(value, "y", 1));
		if (t == typeof(Color))
			return value.ValueKind == JsonValueKind.String ? ParseHexColor(value.GetString()) : new Color((int)Num(value, "r", 0), (int)Num(value, "g", 1), (int)Num(value, "b", 2), (int)Num(value, "a", 3, 255));
		if (t == typeof(Rectangle))
			return new Rectangle((int)Num(value, "x", 0), (int)Num(value, "y", 1), (int)Num(value, "width", 2), (int)Num(value, "height", 3));
		if (t == typeof(RectangleF))
			return new RectangleF(Num(value, "x", 0), Num(value, "y", 1), Num(value, "width", 2), Num(value, "height", 3));
		if (t == typeof(TimeSpan))
			return value.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(value.GetDouble()) : ParseTimeSpan(Text(value));
		if (t.IsEnum)
			return Enum.TryParse(t, Text(value), true, out var parsed) && parsed != null
				? parsed
				: throw new GatewayException($"'{Text(value)}' is not a {t.Name}; use one of {string.Join(", ", Enum.GetNames(t))}");
		if (t == typeof(string))
			return value.ValueKind == JsonValueKind.Null ? null : Text(value);
		if (t == typeof(bool))
			return value.ValueKind == JsonValueKind.String ? bool.Parse(value.GetString()) : value.GetBoolean();

		try
		{
			return System.Convert.ChangeType(Text(value), t, CultureInfo.InvariantCulture);
		}
		catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
		{
			throw new GatewayException($"cannot convert '{Text(value)}' to {t.Name}");
		}
	}

	/// <summary>Rejects an asset the member's [AssetType] would not accept in the inspector.</summary>
	private static void CheckAssetType(MemberInfo member, AssetItem item, Guid guid)
	{
		var filter = AssetSlotFilter.For(member);
		if (filter.IsConstrained && !filter.Accepts(item.AbsolutePath, guid))
			throw new GatewayException($"{member.Name} expects a {filter.DisplayTypeName} asset; {item.FileName} does not match");
	}

	private static Color ParseHexColor(string text)
	{
		var hex = text.Trim().TrimStart('#');
		if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
			throw new GatewayException($"colour '{text}' is not #RRGGBB or #RRGGBBAA");
		if (hex.Length == 6)
			packed = (packed << 8) | 0xFF;
		return new Color((int)(packed >> 24 & 0xFF), (int)(packed >> 16 & 0xFF), (int)(packed >> 8 & 0xFF), (int)(packed & 0xFF));
	}

	private static TimeSpan ParseTimeSpan(string text)
	{
		if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
			return TimeSpan.FromSeconds(seconds);
		if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
			return span;
		throw new GatewayException($"'{text}' is not a duration (seconds or hh:mm:ss.fff)");
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
