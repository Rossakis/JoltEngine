using System;
using System.Collections.Generic;
using Nez;
using Nez.ImGuiTools.UndoActions;

public class TmxClearUndoAction : EditorChangeTracker.IEditorAction
{
    private readonly Scene _scene;
    private readonly List<Entity> _clearedEntities;
    private readonly string _clearedTmxFileName;
    private readonly string _description;

    public string Description => _description;

    public TmxClearUndoAction(
        Scene scene,
        List<Entity> clearedEntities,
        string clearedTmxFileName,
        string description)
    {
        _scene = scene;
        _clearedEntities = new List<Entity>(clearedEntities);
        _clearedTmxFileName = clearedTmxFileName;
        _description = description;
    }

    public void Undo()
    {
        if (_scene == null)
            return;

        // Restore cleared entities
        foreach (var entity in _clearedEntities)
        {
            if (entity != null && entity.Scene != _scene)
            {
                entity.AttachToScene(_scene);
            }
        }

        // Restore TMX filename
        if (_scene.SceneData != null)
        {
            _scene.SceneData.TiledMapFileName = _clearedTmxFileName;
        }
    }

    public void Redo()
    {
        if (_scene == null)
            return;

        // Clear entities again
        foreach (var entity in _clearedEntities)
        {
            if (entity != null && entity.Scene == _scene)
            {
                entity.Destroy();
            }
        }

        // Clear TMX filename
        if (_scene.SceneData != null)
        {
            _scene.SceneData.TiledMapFileName = "";
        }
    }
}