using System;
using System.Reflection;
using Nez.ImGuiTools;

public class GenericValueChangeAction : EditorChangeTracker.IEditorAction
{
    private readonly object _target;
    private readonly MemberInfo _member;
    private readonly object _oldValue;
    private readonly object _newValue;
    private readonly string _description;

    public string Description => _description;

    public GenericValueChangeAction(object target, MemberInfo member, object oldValue, object newValue, string description)
    {
        _target = target;
        _member = member;
        _oldValue = oldValue;
        _newValue = newValue;
        _description = description;
    }

    public void Undo() => SetValue(_oldValue);
    public void Redo() => SetValue(_newValue);

    private void SetValue(object value)
    {
        switch (_member)
        {
            case PropertyInfo prop:
                prop.SetValue(_target, value);
                break;
            case FieldInfo field:
                field.SetValue(_target, value);
                break;
        }
    }
}