using Microsoft.Xna.Framework;
using Nez;

namespace Nez.ImGuiTools.UndoActions
{
	public class BoxColliderResizeUndoAction : EditorChangeTracker.IEditorAction
	{
		private BoxCollider _collider;
		private RectangleF _originalBounds;
		private RectangleF _newBounds;
		private string _description;
		public string Description => _description;

		public BoxColliderResizeUndoAction(BoxCollider collider, RectangleF originalBounds, RectangleF newBounds, string description)
		{
			_collider = collider;
			_originalBounds = originalBounds;
			_newBounds = newBounds;
			_description = description;
		}

		public void Undo()
		{
			ApplyBounds(_originalBounds);
		}

		public void Redo()
		{
			ApplyBounds(_newBounds);
		}

		private void ApplyBounds(RectangleF bounds)
		{
			if (_collider == null || _collider.Entity == null)
				return;

			var box = _collider.Shape as PhysicsShapes.Box;
			if (box == null)
				return;

			var entityPos = _collider.Entity.Transform.Position;
			var centerX = bounds.X + bounds.Width * 0.5f;
			var centerY = bounds.Y + bounds.Height * 0.5f;

			_collider.LocalOffset = new Vector2(centerX - entityPos.X, centerY - entityPos.Y);
			box.UpdateBox(bounds.Width, bounds.Height);

			if (_collider.Enabled)
			{
				Physics.UpdateCollider(_collider);
			}
		}
	}
}