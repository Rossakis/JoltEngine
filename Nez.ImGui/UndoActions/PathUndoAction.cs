using System;
using System.Collections.Generic;
using System.Reflection;
using Nez.ImGuiTools.UndoActions;

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

    private void SetValue(object value)
    {
        object current = _root;
        Type currentType = current.GetType();

        // Traverse the path except for the last member
        for (int i = 0; i < _path.Count - 1; i++)
        {
            string memberName = _path[i];
            var member = currentType.GetProperty(memberName) as MemberInfo
                      ?? (MemberInfo)currentType.GetField(memberName);

            if (member is PropertyInfo prop)
                current = prop.GetValue(current);
            else if (member is FieldInfo field)
                current = field.GetValue(current);
            else
                throw new InvalidOperationException($"Member '{memberName}' not found on type '{currentType.Name}'.");

            if (current == null)
                throw new InvalidOperationException($"Null encountered while traversing path at '{memberName}'.");
            currentType = current.GetType();
        }

        // Set the value on the last member
        string lastMemberName = _path[^1];
        var lastMember = currentType.GetProperty(lastMemberName) as MemberInfo
                      ?? (MemberInfo)currentType.GetField(lastMemberName);

		if (lastMember is PropertyInfo lastProp)
            lastProp.SetValue(current, value);
        else if (lastMember is FieldInfo lastField)
            lastField.SetValue(current, value);
        else
            throw new InvalidOperationException($"Member '{lastMemberName}' not found on type '{currentType.Name}'.");
    }
}