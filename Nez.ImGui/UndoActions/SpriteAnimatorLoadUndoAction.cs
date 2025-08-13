using Nez.Sprites;
using Nez.ImGuiTools.UndoActions;

public class SpriteAnimatorLoadUndoAction : EditorChangeTracker.IEditorAction
{
    private SpriteAnimator _animator;
    private string _oldFilePath;
    private SpriteAnimator.SpriteAnimatorComponentData _oldData;
    private string _newFilePath;
    private SpriteAnimator.SpriteAnimatorComponentData _newData;
    private string _description;
    public string Description => _description;

	public SpriteAnimatorLoadUndoAction(
        SpriteAnimator animator,
        string oldFilePath,
        SpriteAnimator.SpriteAnimatorComponentData oldData,
        string newFilePath,
        SpriteAnimator.SpriteAnimatorComponentData newData,
        string description)
    {
        _animator = animator;
        _oldFilePath = oldFilePath;
        _oldData = oldData;
        _newFilePath = newFilePath;
        _newData = newData;
        _description = description;
    }

    public void Undo()
    {
        _animator.TextureFilePath = _oldFilePath;
        _animator.Data = _oldData;
    }

    public void Redo()
    {
        _animator.TextureFilePath = _newFilePath;
        _animator.Data = _newData;
    }
}