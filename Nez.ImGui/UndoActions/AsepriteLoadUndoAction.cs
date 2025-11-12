using System.Collections.Generic;
using Nez;
using Nez.Editor;

namespace Nez.ImGuiTools.UndoActions
{
    public class AsepriteLoadUndoAction : EditorChangeTracker.IEditorAction
    {
        private Scene _scene;
        private List<Entity> _createdEntities;
        private Entity _parentEntity;
        private string _asepriteFilePath;
        private List<string> _layerNames;
        private string _description;

        public AsepriteLoadUndoAction(
            Scene scene,
            List<Entity> createdEntities,
            Entity parentEntity,
            string asepriteFilePath,
            List<string> layerNames,
            string description)
        {
            _scene = scene;
            _createdEntities = new List<Entity>(createdEntities);
            _parentEntity = parentEntity;
            _asepriteFilePath = asepriteFilePath;
            _layerNames = new List<string>(layerNames);
            _description = description;
        }

        public string Description => _description;

        public void Undo()
        {
            // Remove all created entities
            foreach (var entity in _createdEntities)
            {
                if (entity != null && entity.Scene == _scene)
                {
                    entity.Destroy();
                }
            }
        }

        public void Redo()
        {
            // This would require re-creating all the entities
            // For simplicity, we can show a notification that manual reload is needed
            NotificationSystem.ShowTimedNotification(
                $"Please reload Aseprite file manually: {System.IO.Path.GetFileName(_asepriteFilePath)}"
            );
        }
    }
}