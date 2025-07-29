using System;
using System.Linq.Expressions;
using System.Reflection;
public static class StructSetterBuilder
{
    /// <summary>
    /// Builds a delegate that sets a nested field or property on a struct, updating all parents up to the root.
    /// </summary>
    /// <param name="rootType">Type of the root object</param>
    /// <param name="memberPath">Array of MemberInfo representing the path to the nested member</param>
    /// <returns>Action&lt;object, object&gt; that sets the value</returns>
    public static Action<object, object> BuildSetter(Type rootType, MemberInfo[] memberPath)
    {
        var rootParam = Expression.Parameter(typeof(object), "root");
        var valueParam = Expression.Parameter(typeof(object), "value");

        // Start with the root object, cast from object to rootType
        Expression current = Expression.Convert(rootParam, rootType);

        var variables = new System.Collections.Generic.List<ParameterExpression>();
        var assigns = new System.Collections.Generic.List<Expression>();

        // Walk down the path, keeping track of each struct
        for (int i = 0; i < memberPath.Length - 1; i++)
        {
            var member = memberPath[i];
            var memberType = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo)member).PropertyType;
            var varExpr = Expression.Variable(memberType, $"level{i}");
            variables.Add(varExpr);

            // Access the field/property on the current object (no cast to memberType!)
            Expression getMember = member is FieldInfo
                ? Expression.Field(current, (FieldInfo)member)
                : Expression.Property(current, (PropertyInfo)member);

            assigns.Add(Expression.Assign(varExpr, getMember));
            current = varExpr;
        }

        // Set the value on the last member
        var lastMember = memberPath[^1];

        Expression setValue = lastMember is FieldInfo
            ? Expression.Assign(
                Expression.Field(current, (FieldInfo)lastMember),
                Expression.Convert(valueParam, ((FieldInfo)lastMember).FieldType))
            : Expression.Assign(
                Expression.Property(current, (PropertyInfo)lastMember),
                Expression.Convert(valueParam, ((PropertyInfo)lastMember).PropertyType));

        assigns.Add(setValue);

        // Now, walk back up and assign each struct back to its parent
        for (int i = memberPath.Length - 2; i >= 0; i--)
        {
            Expression parent = i == 0
                ? (Expression)Expression.Convert(rootParam, rootType)
                : variables[i - 1];

            var member = memberPath[i];
            var child = variables[i];

            Expression assignBack = member is FieldInfo
                ? Expression.Assign(Expression.Field(parent, (FieldInfo)member), child)
                : Expression.Assign(Expression.Property(parent, (PropertyInfo)member), child);

            assigns.Add(assignBack);
        }

        var body = Expression.Block(variables, assigns);
        var lambda = Expression.Lambda<Action<object, object>>(body, rootParam, valueParam);
        return lambda.Compile();
    }
}