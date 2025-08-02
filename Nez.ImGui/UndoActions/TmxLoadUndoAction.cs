using System;
using System.Collections.Generic;
using System.Linq;
using Nez;
using Nez.ImGuiTools.UndoActions;

public class TmxLoadUndoAction : EditorChangeTracker.IEditorAction
{
    private readonly Scene _scene;
    private readonly List<Entity> _createdEntities;
    private readonly Entity _tiledMapEntity;
    private readonly string _newTmxFileName;
    private readonly string _oldTmxFileName;
    private readonly List<Entity> _oldTmxEntities;
    private readonly string _description;

    public string Description => _description;

    public TmxLoadUndoAction(
        Scene scene,
        List<Entity> createdEntities,
        Entity tiledMapEntity,
        string newTmxFileName,
        string oldTmxFileName,
        List<Entity> oldTmxEntities,
        string description)
    {
        _scene = scene;
        _createdEntities = new List<Entity>(createdEntities); // Create a copy
        _tiledMapEntity = tiledMapEntity;
        _newTmxFileName = newTmxFileName;
        _oldTmxFileName = oldTmxFileName;
        _oldTmxEntities = oldTmxEntities != null ? new List<Entity>(oldTmxEntities) : new List<Entity>();
        _description = description;
    }

    public void Undo()
    {
        if (_scene == null)
            return;

        // Remove all entities created by the new TMX load
        foreach (var entity in _createdEntities.ToList()) // ToList to avoid modification during iteration
        {
            if (entity != null && entity.Scene == _scene)
            {
                // Clear any parent relationships before destroying
                entity.Transform.SetParent(null);
                entity.Destroy();
            }
        }

        // Restore old TMX entities if they existed
        if (_oldTmxEntities.Count > 0)
        {
            foreach (var entity in _oldTmxEntities)
            {
                if (entity != null && entity.Scene != _scene)
                {
                    entity.AttachToScene(_scene);
                    // Note: Parent relationships would need to be restored here too
                    // if we stored them in the undo data
                }
            }
        }

        // Restore old TMX filename
        if (_scene.SceneData != null)
        {
            _scene.SceneData.TiledMapFileName = _oldTmxFileName;
        }
    }

    public void Redo()
    {
        if (_scene == null)
            return;

        // Remove old TMX entities
        foreach (var entity in _oldTmxEntities.ToList())
        {
            if (entity != null && entity.Scene == _scene)
            {
                // Clear any parent relationships before destroying
                entity.Transform.SetParent(null);
                entity.Destroy();
            }
        }

        // Restore new TMX entities
        foreach (var entity in _createdEntities)
        {
            if (entity != null && entity.Scene != _scene)
            {
                entity.AttachToScene(_scene);
            }
        }

        // Now restore parent relationships for TMX entities
        if (_tiledMapEntity != null && _tiledMapEntity.Scene == _scene)
        {
            foreach (var entity in _createdEntities)
            {
                if (entity != null && entity != _tiledMapEntity && entity.Scene == _scene)
                {
                    // Set the TiledMapEntity as parent for all child entities (excluding the TiledMapEntity itself)
                    entity.Transform.SetParent(_tiledMapEntity.Transform);
                }
            }
        }

        // Restore new TMX filename
        if (_scene.SceneData != null)
        {
            _scene.SceneData.TiledMapFileName = _newTmxFileName;
        }
    }
}