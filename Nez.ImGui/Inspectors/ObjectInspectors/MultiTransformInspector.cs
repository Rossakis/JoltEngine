using ImGuiNET;
using System.Collections.Generic;
using System.Linq;
using Nez;
using Num = System.Numerics;

public class MultiTransformInspector
{
    private List<Entity> _entities;
    private List<Transform> _transforms;

    public MultiTransformInspector(List<Entity> entities)
    {
        _transforms = entities.Select(e => e.Transform).ToList();
    }

    public void Draw()
    {
        ImGui.PushID("MultiTransformInspector");
        if (ImGui.CollapsingHeader("Transform"))
        {
            // Use the first transform as reference
            var refTransform = _transforms[0];
            var position = new Num.Vector2(refTransform.Position.X, refTransform.Position.Y);
            var rotation = refTransform.Rotation;
            var scale = new Num.Vector2(refTransform.Scale.X, refTransform.Scale.Y);

            bool posChanged = ImGui.InputFloat2("Position", ref position);
            bool rotChanged = ImGui.InputFloat("Rotation", ref rotation);
            bool scaleChanged = ImGui.InputFloat2("Scale", ref scale);

            if (posChanged || rotChanged || scaleChanged)
            {
                foreach (var t in _transforms)
                {
                    if (posChanged) t.Position = new Microsoft.Xna.Framework.Vector2(position.X, position.Y);
                    if (rotChanged) t.Rotation = rotation;
                    if (scaleChanged) t.Scale = new Microsoft.Xna.Framework.Vector2(scale.X, scale.Y);
                }
                // Optionally: add undo support here
            }
        }
        ImGui.PopID();
    }
}