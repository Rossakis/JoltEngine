using System.Collections.Generic;

// public static class AccessorPathUtils
// {
//     public static object GetValueFromPath(object root, List<IAccessor> path)
//     {
//         object current = root;
//         foreach (var accessor in path)
//             current = accessor.Get(current);
//         return current;
//     }
//
//     public static object SetValueFromPath(object root, List<IAccessor> path, object value)
//     {
//         // Walk down, collecting parents
//         var parents = new object[path.Count + 1];
//         parents[0] = root;
//         for (int i = 0; i < path.Count; i++)
//             parents[i + 1] = path[i].Get(parents[i]);
//
//         // Rebuild from bottom up
//         object currentValue = value;
//         for (int i = path.Count - 1; i >= 0; i--)
//             currentValue = path[i].Set(parents[i], currentValue);
//
//         return currentValue;
//     }
// }