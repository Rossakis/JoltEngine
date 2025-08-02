using Nez;
using Nez.ECS;
using Nez.ImGuiTools.UndoActions;
using System;

public class EntityCreateDeleteUndoAction : EditorChangeTracker.IEditorAction
{
    private readonly Scene _scene;
    private readonly Entity _entity;
    private readonly bool _wasCreated; // true = creation, false = deletion
    private readonly string _description;

    public string Description => _description;

    public EntityCreateDeleteUndoAction(Scene scene, Entity entity, bool wasCreated, string description)
    {
        _scene = scene;
        _entity = entity;
        _wasCreated = wasCreated;
        _description = description;
    }

    public void Undo()
    {
	    if (_wasCreated)
		    _entity.DetachFromScene();
	    else
		    _entity.AttachToScene(_scene);
    }

    public void Redo()
    {
	    if (_wasCreated)
		    _entity.AttachToScene(_scene);
	    else
		    _entity.DetachFromScene();
    }

	private void DuplicateEntity(Entity entity)
	{
		if (entity == null || entity.Scene == null)
			return;

		var typeName = entity.GetType().Name;
		if (EntityFactoryRegistry.TryCreate(typeName, out var clone))
		{
			EntityFactoryRegistry.InvokeEntityCreated(clone);
			clone.Type = Entity.InstanceType.Dynamic;
			clone.Name = Core.Scene.GetUniqueEntityName(typeName);
			clone.Transform.Position = entity.Transform.Position;
			clone.Transform.Rotation = entity.Rotation;
			clone.Transform.Scale = entity.Scale;
			clone.SetTag(entity.Tag);
			clone.Enabled = entity.Enabled;
			clone.UpdateOrder = entity.UpdateOrder;
			clone.DebugRenderEnabled = entity.DebugRenderEnabled;

			// Undo/Redo support for entity creation
			EditorChangeTracker.PushUndo(
				new EntityCreateDeleteUndoAction(entity.Scene, clone, wasCreated: true, $"Create Entity {clone.Name}"),
				clone,
				$"Create Entity {clone.Name}"
			);
		}
		else
		{
			throw new InvalidOperationException(
				$"EntityFactoryRegistry: Entity type '{typeName}' is not registered in the factory. " +
				$"Did you forget to call EntityFactoryRegistry.Register(\"{typeName}\", ...)?");
		}
	}
}