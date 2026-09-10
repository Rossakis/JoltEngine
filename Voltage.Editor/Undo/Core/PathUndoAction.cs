using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Voltage.Editor.Undo.Core;

/// <summary>Restores one value addressed by member names and list indices ("Bindings", "[0]", "Entity") from a root object.</summary>
public class PathUndoAction : EditorChangeTracker.IEditorAction
{
	private readonly object _root;
	private readonly List<string> _path;
	private readonly object _oldValue;
	private readonly object _newValue;
	private readonly string _description;

	public string Description => _description;

	public PathUndoAction(object root, List<string> path, object oldValue, object newValue, string description)
	{
		_root = root;
		_path = path;
		_oldValue = oldValue;
		_newValue = newValue;
		_description = description;
	}

	public void Undo() => SetValue(_oldValue);
	public void Redo() => SetValue(_newValue);

	private static bool IsIndex(string segment, out int index)
	{
		index = -1;
		return segment.Length > 2 && segment[0] == '[' && segment[^1] == ']' && int.TryParse(segment.AsSpan(1, segment.Length - 2), out index);
	}

	private void SetValue(object value)
	{
		// Each level is kept so a boxed struct along the path can be written back after the leaf changes.
		var holders = new List<object> { _root };
		var members = new List<MemberInfo>();
		var indices = new List<int>();
		object current = _root;

		for (int i = 0; i < _path.Count - 1; i++)
		{
			var segment = _path[i];
			object next;
			MemberInfo member = null;
			if (IsIndex(segment, out var index))
				next = ((IList)current)[index];
			else
			{
				member = Find(current.GetType(), segment);
				next = member is PropertyInfo prop ? prop.GetValue(current) : ((FieldInfo)member).GetValue(current);
			}

			if (next == null)
				throw new InvalidOperationException($"Null encountered while traversing path at '{segment}'.");
			current = next;
			holders.Add(current);
			members.Add(member);
			indices.Add(index);
		}

		var last = _path[^1];
		if (IsIndex(last, out var lastIndex))
			((IList)current)[lastIndex] = value;
		else
		{
			var lastMember = Find(current.GetType(), last);
			if (lastMember is PropertyInfo lastProp)
				lastProp.SetValue(current, value);
			else
				((FieldInfo)lastMember).SetValue(current, value);
		}

		for (int i = members.Count - 1; i >= 0; i--)
		{
			if (!holders[i + 1].GetType().IsValueType)
				continue;
			if (members[i] == null)
				((IList)holders[i])[indices[i]] = holders[i + 1];
			else if (members[i] is PropertyInfo p && p.CanWrite)
				p.SetValue(holders[i], holders[i + 1]);
			else if (members[i] is FieldInfo f)
				f.SetValue(holders[i], holders[i + 1]);
		}
	}

	private static MemberInfo Find(Type type, string name) =>
		(MemberInfo)type.GetProperty(name) ?? type.GetField(name)
		?? throw new InvalidOperationException($"Member '{name}' not found on type '{type.Name}'.");
}
