using System;
using Nez.ImGuiTools.UndoActions;
using Nez.Sprites;
using Nez.Textures;

public class SpriteLoadUndoAction : EditorChangeTracker.IEditorAction
{
    private readonly SpriteRenderer _spriteRenderer;
    private readonly Sprite _oldSprite;
    private readonly SpriteRenderer.SpriteRendererComponentData _oldData;
    private readonly Sprite _newSprite;
    private readonly SpriteRenderer.SpriteRendererComponentData _newData;
    private readonly string _description;

    public string Description => _description;

    public SpriteLoadUndoAction(
        SpriteRenderer spriteRenderer,
        Sprite oldSprite,
        SpriteRenderer.SpriteRendererComponentData oldData,
        Sprite newSprite,
        SpriteRenderer.SpriteRendererComponentData newData,
        string description)
    {
        _spriteRenderer = spriteRenderer;
        _oldSprite = oldSprite;
        _oldData = oldData;
        _newSprite = newSprite;
        _newData = newData;
        _description = description;
    }

    public void Undo()
    {
        if (_spriteRenderer == null || _spriteRenderer.Entity == null)
            return;

        // Restore the old sprite
        _spriteRenderer.SetSprite(_oldSprite);
        
        // Restore the old component data
        if (_oldData != null)
        {
            _spriteRenderer.Data = _oldData;
            
            // Apply the old data properties to the component
            _spriteRenderer.Color = _oldData.Color;
            _spriteRenderer.LocalOffset = _oldData.LocalOffset;
            _spriteRenderer.Origin = _oldData.Origin;
            _spriteRenderer.LayerDepth = _oldData.LayerDepth;
            _spriteRenderer.RenderLayer = _oldData.RenderLayer;
            _spriteRenderer.SetEnabled(_oldData.Enabled);
            _spriteRenderer.SpriteEffects = _oldData.SpriteEffects;
        }
    }

    public void Redo()
    {
        if (_spriteRenderer == null || _spriteRenderer.Entity == null)
            return;

        // Restore the new sprite
        _spriteRenderer.SetSprite(_newSprite);
        
        // Restore the new component data
        if (_newData != null)
        {
            _spriteRenderer.Data = _newData;
            
            // Apply the new data properties to the component
            _spriteRenderer.Color = _newData.Color;
            _spriteRenderer.LocalOffset = _newData.LocalOffset;
            _spriteRenderer.Origin = _newData.Origin;
            _spriteRenderer.LayerDepth = _newData.LayerDepth;
            _spriteRenderer.RenderLayer = _newData.RenderLayer;
            _spriteRenderer.SetEnabled(_newData.Enabled);
            _spriteRenderer.SpriteEffects = _newData.SpriteEffects;
        }
    }
}