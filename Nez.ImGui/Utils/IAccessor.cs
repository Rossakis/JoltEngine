using System;
using System.Collections;
using System.Reflection;

public interface IAccessor
{
    object Get(object parent);
    object Set(object parent, object newChild); // returns the updated parent
}

public class FieldAccessor : IAccessor
{
    private readonly FieldInfo _field;
    public FieldAccessor(FieldInfo field) => _field = field;

    public object Get(object parent) => _field.GetValue(parent);

    public object Set(object parent, object newChild)
    {
        // For structs, parent may be a boxed value type, so we need to copy
        object copy = parent;
        _field.SetValue(copy, newChild);
        return copy;
    }
}

public class PropertyAccessor : IAccessor
{
    private readonly PropertyInfo _property;
    public PropertyAccessor(PropertyInfo property) => _property = property;

    public object Get(object parent) => _property.GetValue(parent);

    public object Set(object parent, object newChild)
    {
        object copy = parent;
        _property.SetValue(copy, newChild);
        return copy;
    }
}

public class ListAccessor : IAccessor
{
    private readonly int _index;
    public ListAccessor(int index) => _index = index;

    public object Get(object parent)
    {
        var list = (IList)parent;
        return list[_index];
    }

    public object Set(object parent, object newChild)
    {
        var list = (IList)parent;
        list[_index] = newChild;
        return list;
    }
}

public class DictionaryAccessor : IAccessor
{
    private readonly object _key;
    public DictionaryAccessor(object key) => _key = key;

    public object Get(object parent)
    {
        var dict = (IDictionary)parent;
        return dict[_key];
    }

    public object Set(object parent, object newChild)
    {
        var dict = (IDictionary)parent;
        dict[_key] = newChild;
        return dict;
    }
}