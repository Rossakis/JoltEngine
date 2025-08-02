using System.Collections.Generic;
using Nez.ImGuiTools.UndoActions;

public static class UndoActionHelpers
{
    /// <summary>
    /// Creates an undo action for a nested struct value change.
    /// Example: UndoActionHelpers.CreateNestedStructUndo(playerData, "DashValues.SlideSpeed", oldValue, newValue, "DashValues.SlideSpeed");
    /// </summary>
    public static NestedStructUndoAction CreateNestedStructUndo(object root, string dotPath, object oldValue, object newValue, string description)
    {
        var path = new List<string>(dotPath.Split('.'));
        return new NestedStructUndoAction(root, path, oldValue, newValue, description);
    }
    
    /// <summary>
    /// Creates an undo action for a nested struct value change using a path list.
    /// </summary>
    public static NestedStructUndoAction CreateNestedStructUndo(object root, List<string> path, object oldValue, object newValue, string description)
    {
        return new NestedStructUndoAction(root, path, oldValue, newValue, description);
    }
}