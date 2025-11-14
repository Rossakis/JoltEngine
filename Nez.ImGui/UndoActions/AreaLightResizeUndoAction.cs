using Microsoft.Xna.Framework;
using Nez;
using Nez.DeferredLighting;
using Nez.Editor;

namespace Nez.ImGuiTools.UndoActions
{
	public class AreaLightResizeUndoAction : EditorChangeTracker.IEditorAction
	{
		private AreaLight _areaLight;
		private RectangleF _originalBounds;
		private RectangleF _newBounds;
		private string _description;
		public string Description => _description;

		public AreaLightResizeUndoAction(AreaLight areaLight, RectangleF originalBounds, RectangleF newBounds, string description)
		{
			_areaLight = areaLight;
			_originalBounds = originalBounds;
			_newBounds = newBounds;
			_description = description;
		}

		public void Undo()
		{
			if (_areaLight == null || _areaLight.Entity == null)
				return;

			var scale = _areaLight.Entity.Transform.Scale;
			var width = _originalBounds.Width / scale.X;
			var height = _originalBounds.Height / scale.Y;

			_areaLight.SetWidth(width);
			_areaLight.SetHeight(height);

			var center = new Vector2(
				_originalBounds.X + _originalBounds.Width / 2f,
				_originalBounds.Y + _originalBounds.Height / 2f
			);
			_areaLight.Entity.Transform.Position = center;
		}

		public void Redo()
		{
			if (_areaLight == null || _areaLight.Entity == null)
				return;

			var scale = _areaLight.Entity.Transform.Scale;
			var width = _newBounds.Width / scale.X;
			var height = _newBounds.Height / scale.Y;

			_areaLight.SetWidth(width);
			_areaLight.SetHeight(height);

			var center = new Vector2(
				_newBounds.X + _newBounds.Width / 2f,
				_newBounds.Y + _newBounds.Height / 2f
			);
			_areaLight.Entity.Transform.Position = center;
		}
	}
}