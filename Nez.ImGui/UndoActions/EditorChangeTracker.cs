using System.Collections.Generic;
using ImGuiNET;

namespace Nez.ImGuiTools.UndoActions;

/// <summary>
/// Tracks editor changes (dirty state) and supports undo/redo actions.
/// </summary>
public class EditorChangeTracker
{
	// Dirty State Tracking
	public static bool IsDirty => _changedObjects.Count > 0;

    private static readonly List<(object obj, string description)> _changedObjects = new();

    /// <summary>
    /// List of objects that have unsaved changes.
    /// </summary>
    public static IReadOnlyList<(object obj, string description)> ChangedObjects => _changedObjects;

    /// <summary>
    /// Mark an object as changed (dirty).
    /// </summary>
    public static void MarkChanged(object obj, string description)
    {
        if (!_changedObjects.Exists(x => x.obj == obj))
            _changedObjects.Add((obj, description));
    }

    /// <summary>
    /// Clear all tracked changes (reset dirty state).
    /// </summary>
    public static void Clear()
    {
        _changedObjects.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
    }

    /// <summary>
    /// Clear the Redo stack and reset the IsDirty flag on Scene Save (Undo stack is untouched).
    /// </summary>
    public static void ClearOnSave()
    {
	    _changedObjects.Clear();
	    _redoStack.Clear();
    }

	// Undo/Redo Tracking

	/// <summary>
	/// Represents an undoable/redoable action.
	/// </summary>
	public interface IEditorAction
    {
        void Undo();
        void Redo();
        string Description { get; }
    }

    private static readonly Stack<IEditorAction> _undoStack = new();
    private static readonly Stack<IEditorAction> _redoStack = new();

    /// <summary>
    /// Returns true if there are actions to undo.
    /// </summary>
    public static bool CanUndo => _undoStack.Count > 0;

    /// <summary>
    /// Returns true if there are actions to redo.
    /// </summary>
    public static bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Returns a read-only list of undo actions (top is last).
    /// </summary>
    public static IReadOnlyCollection<IEditorAction> UndoActions => _undoStack;

    /// <summary>
    /// Returns a read-only list of redo actions (top is last).
    /// </summary>
    public static IReadOnlyCollection<IEditorAction> RedoActions => _redoStack;

    /// <summary>
    /// Pushes a new action onto the undo stack and clears the redo stack.
    /// Also marks the object as changed (dirty).
    /// </summary>
    public static void PushUndo(IEditorAction action, object changedObj = null, string description = null)
    {
        // Always allow undo - remove the ShouldAllowUndo check entirely
        _undoStack.Push(action);
        _redoStack.Clear();
        if (changedObj != null && description != null)
            MarkChanged(changedObj, description);
    }

    /// <summary>
    /// Performs an undo if possible. Returns the undone action, or null if none.
    /// Also marks the affected object as changed (dirty).
    /// </summary>
    public static IEditorAction Undo()
    {
        if (!CanUndo)
            return null;

        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);

        return action;
    }

    /// <summary>
    /// Performs a redo if possible. Returns the redone action, or null if none.
    /// Also marks the affected object as changed (dirty).
    /// </summary>
    public static IEditorAction Redo()
    {
        if (!CanRedo)
            return null;

        var action = _redoStack.Pop();
        action.Redo();
        _undoStack.Push(action);

        return action;
    }

    /// <summary>
    /// Reverts all actions and clears the dirty state.
    /// </summary>
    public static void Revert()
    {
        // Undo all actions
        while (CanUndo)
            Undo();

        // Clear dirty state
        Clear();
    }
}