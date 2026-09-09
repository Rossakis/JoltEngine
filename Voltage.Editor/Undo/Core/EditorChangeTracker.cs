using System;
using System.Collections.Generic;
using System.Linq;
using Voltage.Data;

namespace Voltage.Editor.Undo.Core;

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
        // Data asset edits are not scene changes; they stay listed as unsaved.
        _changedObjects.RemoveAll(x => !(x.obj is DataAsset));

        // Data asset history outlives scene loads and play mode; scene-scoped history goes.
        KeepOnlyDataAssets(_undoStack);
        KeepOnlyDataAssets(_redoStack);
    }

    private static void KeepOnlyDataAssets(Stack<Entry> stack)
    {
        if (stack.Count == 0)
            return;

        // Stacks enumerate top-first, so re-push in reverse to keep the original order.
        var kept = stack.Where(e => e.Target is DataAsset).Reverse().ToList();
        stack.Clear();
        foreach (var entry in kept)
            stack.Push(entry);
    }

    /// <summary>
    /// Clear the Redo stack and reset the IsDirty flag on Scene Save (Undo stack is untouched).
    /// </summary>
    public static void ClearOnSave()
    {
	    // Saving the scene says nothing about a data asset, which is a separate file with its own save.
	    _changedObjects.RemoveAll(x => !(x.obj is DataAsset));
	    _redoStack.Clear();
    }

    /// <summary>Drops the dirty mark for one object, e.g. after a data asset is saved on its own.</summary>
    public static void ClearChangesFor(object obj)
    {
        if (obj != null)
            _changedObjects.RemoveAll(x => x.obj == obj);
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

    /// <summary>An action plus the object it edits, so Clear can tell scene history from data asset history.</summary>
    private readonly record struct Entry(IEditorAction Action, object Target);

    private static readonly Stack<Entry> _undoStack = new();
    private static readonly Stack<Entry> _redoStack = new();

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
    public static IReadOnlyCollection<IEditorAction> UndoActions => _undoStack.Select(e => e.Action).ToList();

    /// <summary>
    /// Returns a read-only list of redo actions (top is last).
    /// </summary>
    public static IReadOnlyCollection<IEditorAction> RedoActions => _redoStack.Select(e => e.Action).ToList();

    /// <summary>
    /// Pushes a new action onto the undo stack and clears the redo stack.
    /// Also marks the object as changed (dirty).
    /// </summary>
    public static void PushUndo(IEditorAction action, object changedObj = null, string description = null)
    {
        if (_group != null)
        {
            _group.Add(action);
            _groupTarget ??= changedObj;
        }
        else
        {
            _undoStack.Push(new Entry(action, changedObj));
            _redoStack.Clear();
        }
        if (changedObj != null && description != null)
            MarkChanged(changedObj, description);
    }

    private static List<IEditorAction> _group;
    private static string _groupDescription;
    private static object _groupTarget;

    /// <summary>True while <see cref="BeginGroup"/> is collecting actions.</summary>
    public static bool InGroup => _group != null;

    /// <summary>Collects every PushUndo until <see cref="EndGroup"/> into one undo step.</summary>
    public static void BeginGroup(string description)
    {
        if (_group != null)
            throw new InvalidOperationException("an undo group is already open");
        _group = new List<IEditorAction>();
        _groupDescription = description;
        _groupTarget = null;
    }

    /// <summary>Closes the group; returns how many actions it folded, or -1 when none was open. An empty group pushes nothing.</summary>
    public static int EndGroup()
    {
        if (_group == null)
            return -1;

        var actions = _group;
        var target = _groupTarget;
        var description = _groupDescription;
        _group = null;
        _groupTarget = null;
        if (actions.Count > 0)
        {
            _undoStack.Push(new Entry(new CompositeUndoAction(actions, description ?? $"{actions.Count} changes"), target));
            _redoStack.Clear();
        }
        return actions.Count;
    }

    /// <summary>
    /// Performs an undo if possible. Returns the undone action, or null if none.
    /// Also marks the affected object as changed (dirty).
    /// </summary>
    public static IEditorAction Undo()
    {
        if (!CanUndo)
            return null;

        var entry = _undoStack.Pop();
        entry.Action.Undo();
        _redoStack.Push(entry);
        NoteApplied(entry);

        return entry.Action;
    }

    /// <summary>
    /// Performs a redo if possible. Returns the redone action, or null if none.
    /// Also marks the affected object as changed (dirty).
    /// </summary>
    public static IEditorAction Redo()
    {
        if (!CanRedo)
            return null;

        var entry = _redoStack.Pop();
        entry.Action.Redo();
        _undoStack.Push(entry);
        NoteApplied(entry);

        return entry.Action;
    }

    /// <summary>Undo/redo writes through reflection, so windows watching for edits are told a value changed.</summary>
    private static void NoteApplied(Entry entry)
    {
        // Write count only: marking dirty would leave an object dirty after undoing it back to its saved state.
        Inspectors.TypeInspectors.AbstractTypeInspector.NoteExternalValueWrite(entry.Target);
    }

    /// <summary>
    /// Reverts all actions and clears the dirty state.
    /// </summary>
    public static void Revert()
    {
        while (CanUndo)
            Undo();

        Clear();
    }
}