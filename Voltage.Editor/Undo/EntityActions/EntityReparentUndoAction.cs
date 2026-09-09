using System.Collections.Generic;
using Voltage.Editor.Undo.Core;

namespace Voltage.Editor.Undo.EntityActions;

/// <summary>
/// Undo/Redo action for reparenting one or more entities, preserving sibling order.
/// </summary>
public class EntityReparentUndoAction : EditorChangeTracker.IEditorAction
{
    private readonly List<(Entity entity, Transform oldParent, int oldIndex, Transform newParent, int newIndex)> _entries;
    private readonly string _description;

    public string Description => _description;

    public EntityReparentUndoAction(
        List<(Entity entity, Transform oldParent, int oldIndex, Transform newParent, int newIndex)> entries,
        string description)
    {
        _entries = entries;
        _description = description;
    }

    /// <summary>MoveEntityToIndex takes the index before removal; indices here are final positions, so a move down needs one more.</summary>
    public static void MoveRootTo(Entity entity, int index)
    {
        var entities = entity.Scene?.Entities;
        if (entities == null || index < 0)
            return;
        var current = entities.EntityFastList.IndexOf(entity);
        entities.MoveEntityToIndex(entity, current >= 0 && index > current ? index + 1 : index);
    }

    public void Undo()
    {
        foreach (var entry in _entries)
        {
            var worldPos = entry.entity.Transform.Position;
            var worldRot = entry.entity.Transform.Rotation;
            var worldScale = entry.entity.Transform.Scale;

            entry.entity.Transform.SetParentAt(entry.oldParent, entry.oldIndex);
            if (entry.oldParent == null)
                MoveRootTo(entry.entity, entry.oldIndex);

            entry.entity.Transform.RecomputeLocalsFromWorld(worldPos, worldRot, worldScale);
        }
    }

    public void Redo()
    {
        foreach (var entry in _entries)
        {
            var worldPos = entry.entity.Transform.Position;
            var worldRot = entry.entity.Transform.Rotation;
            var worldScale = entry.entity.Transform.Scale;

            entry.entity.Transform.SetParentAt(entry.newParent, entry.newIndex);
            if (entry.newParent == null)
                MoveRootTo(entry.entity, entry.newIndex);

            entry.entity.Transform.RecomputeLocalsFromWorld(worldPos, worldRot, worldScale);
        }
    }
}
