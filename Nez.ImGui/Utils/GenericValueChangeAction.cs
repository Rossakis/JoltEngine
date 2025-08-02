using System;
using System.Reflection;
using Nez.ImGuiTools.UndoActions;

public class GenericValueChangeAction : EditorChangeTracker.IEditorAction
{
    private readonly object _target;
    private readonly MemberInfo _member;
    private readonly object _oldValue;
    private readonly object _newValue;
    private readonly string _description;

    // For delegate-based value setting
    private readonly Action<object, object> _setter;

    public string Description => _description;

    // Reflection-based constructor (existing)
    public GenericValueChangeAction(object target, MemberInfo member, object oldValue, object newValue, string description)
    {
        _target = target;
        _member = member;
        _oldValue = oldValue;
        _newValue = newValue;
        _description = description;
    }

    // Delegate-based constructor (new)
    public GenericValueChangeAction(object target, Action<object, object> setter, object oldValue, object newValue, string description)
    {
        _target = target;
        _setter = setter;
        _oldValue = oldValue;
        _newValue = newValue;
        _description = description;
    }

    public void Undo() => SetValue(_oldValue);
    public void Redo() => SetValue(_newValue);

    private void SetValue(object value)
    {
        if (_setter != null)
        {
            _setter(_target, value);
        }
        else
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
}