using System;
using System.Collections.Generic;
using System.Reflection;
using Nez.ImGuiTools.UndoActions;

namespace Nez.ImGuiTools.UndoActions
{
	/// <summary>
	/// Handles undo/redo for values nested inside structs (including nested structs).
	/// Properly handles value type boxing to ensure changes are applied correctly.
	/// </summary>
	public class NestedStructUndoAction : EditorChangeTracker.IEditorAction
	{
		private readonly object _root;
		private readonly List<string> _path;
		private readonly object _oldValue;
		private readonly object _newValue;
		private readonly string _description;

		public string Description => _description;

		public NestedStructUndoAction(object root, List<string> path, object oldValue, object newValue, string description)
		{
			_root = root;
			_path = new List<string>(path); // Make a copy to avoid reference issues
			_oldValue = oldValue;
			_newValue = newValue;
			_description = description;
		}

		public void Undo() => SetNestedValue(_oldValue);
		public void Redo() => SetNestedValue(_newValue);

		private void SetNestedValue(object value)
		{
			if (_path.Count == 0)
				throw new InvalidOperationException("Path cannot be empty");

			if (_path.Count == 1)
			{
				// Direct property/field on root object
				SetDirectValue(_root, _path[0], value);
				return;
			}

			// Navigate to the parent of the target member and update the entire chain
			var pathToParent = new List<string>(_path.GetRange(0, _path.Count - 1));
			var parentValue = GetNestedValue(_root, pathToParent);

			// Set the value on the parent
			SetDirectValue(parentValue, _path[^1], value);

			// Update the entire chain back to root to handle struct value semantics
			UpdateStructChain(_root, pathToParent, parentValue);
		}

		private object GetNestedValue(object current, List<string> path)
		{
			object result = current;

			foreach (string memberName in path)
			{
				if (result == null)
					throw new InvalidOperationException($"Null encountered while traversing path at '{memberName}'");

				result = GetDirectValue(result, memberName);
			}

			return result;
		}

		private void UpdateStructChain(object root, List<string> pathToUpdate, object newValue)
		{
			if (pathToUpdate.Count == 0)
				return;

			if (pathToUpdate.Count == 1)
			{
				// Update the direct member on root
				SetDirectValue(root, pathToUpdate[0], newValue);
				return;
			}

			// Get the parent of the current path
			var parentPath = pathToUpdate.GetRange(0, pathToUpdate.Count - 1);
			var parent = GetNestedValue(root, parentPath);

			// Set the value on the parent
			SetDirectValue(parent, pathToUpdate[^1], newValue);

			// Recursively update the chain
			UpdateStructChain(root, parentPath, parent);
		}

		private object GetDirectValue(object obj, string memberName)
		{
			var type = obj.GetType();

			// Try property first
			var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (property != null && property.CanRead)
			{
				return property.GetValue(obj);
			}

			// Then try field
			var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (field != null)
			{
				return field.GetValue(obj);
			}

			throw new InvalidOperationException($"Member '{memberName}' not found on type '{type.Name}'");
		}

		private void SetDirectValue(object obj, string memberName, object value)
		{
			var type = obj.GetType();

			// Try property first
			var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (property != null && property.CanWrite)
			{
				property.SetValue(obj, value);
				return;
			}

			// Then try field
			var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (field != null)
			{
				field.SetValue(obj, value);
				return;
			}

			throw new InvalidOperationException($"Member '{memberName}' not found or not writable on type '{type.Name}'");
		}
	}
}