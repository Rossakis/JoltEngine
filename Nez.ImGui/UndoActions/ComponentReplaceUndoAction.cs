using Nez.ImGuiTools.UndoActions;

namespace Nez.ImGuiTools.UndoActions
{
    public class ComponentReplaceUndoAction : EditorChangeTracker.IEditorAction
    {
        private readonly Entity _entity;
        private readonly Component _oldComponent;
        private readonly Component _newComponent;
        private readonly string _description;

        public string Description => _description;

        public ComponentReplaceUndoAction(Entity entity, Component oldComponent, Component newComponent, string description)
        {
            _entity = entity;
            _oldComponent = oldComponent;
            _newComponent = newComponent;
            _description = description;
        }

        public void Undo()
        {
            if (_entity != null && _oldComponent != null)
            {
                _entity.ReplaceComponent(_oldComponent);
            }
        }

        public void Redo()
        {
            if (_entity != null && _newComponent != null)
            {
                _entity.ReplaceComponent(_newComponent);
            }
        }
    }
}