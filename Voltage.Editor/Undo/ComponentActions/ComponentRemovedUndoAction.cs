using System.Linq;
using Voltage.Editor.Undo.Core;

namespace Voltage.Editor.Undo.ComponentActions
{
	/// <summary>Undo action for removing a component: undo re-adds the same instance, redo removes it again.</summary>
	public class ComponentRemovedUndoAction : EditorChangeTracker.IEditorAction
	{
		private readonly Entity _entity;
		private readonly Component _component;

		public string Description => $"Remove {_component.GetType().Name} from {_entity?.Name ?? "Unknown Entity"}";

		public ComponentRemovedUndoAction(Entity entity, Component component)
		{
			_entity = entity;
			_component = component;
		}

		public void Undo()
		{
			if (_entity == null || _entity.IsDestroyed || _component == null)
				return;
			// The removal is deferred to the next update; an undo in the same frame just cancels it.
			if (_entity.Components.CancelRemove(_component) || _entity.Components.Contains(_component) || _entity.ComponentsToAdd.Contains(_component))
				return;
			_entity.AddComponent(_component, true);
		}

		public void Redo()
		{
			if (_entity == null || _entity.IsDestroyed || _component == null)
				return;
			var live = _entity.Components.Concat(_entity.ComponentsToAdd).FirstOrDefault(c => ReferenceEquals(c, _component));
			if (live != null)
				_entity.RemoveComponent(live);
		}
	}
}
