using System;
using Nez.ImGuiTools.UndoActions;
using Nez.Persistence;

namespace Nez.ImGuiTools.UndoActions
{
    public class ComponentDataChangeAction : EditorChangeTracker.IEditorAction
    {
        private readonly Component _component;
        private readonly ComponentData _oldData;
        private readonly ComponentData _newData;
        private readonly string _description;

        public string Description => _description;

        public ComponentDataChangeAction(Component component, ComponentData oldData, ComponentData newData, string description)
        {
            _component = component;
            
            // IMPORTANT: Clone the data to avoid reference sharing
            _oldData = CloneComponentData(oldData);
            _newData = CloneComponentData(newData);
            _description = description;
        }

        private ComponentData CloneComponentData(ComponentData data)
        {
            if (data == null) return null;
            
            try
            {
                // Use JSON serialization for reliable cloning
                var jsonSettings = new JsonSettings
                {
                    PrettyPrint = false,
                    TypeNameHandling = TypeNameHandling.Auto,
                    PreserveReferencesHandling = false
                };
                
                var json = Json.ToJson(data, jsonSettings);
                return (ComponentData)Json.FromJson(json, data.GetType());
            }
            catch
            {
                // Fallback: if JSON fails, try ICloneable
                if (data is ICloneable cloneable)
                    return (ComponentData)cloneable.Clone();
                
                // Last resort: return the original (this will have reference sharing issues)
                return data;
            }
        }

        public void Undo()
        {
            if (_component != null && _oldData != null)
            {
                _component.Data = CloneComponentData(_oldData); // Clone again to avoid reference sharing
            }
        }

        public void Redo()
        {
            if (_component != null && _newData != null)
            {
                _component.Data = CloneComponentData(_newData); // Clone again to avoid reference sharing
            }
        }
    }
}