using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Nez;
using Nez.PhysicsShapes;

namespace Nez.ImGuiTools.UndoActions
{
	public class PolygonColliderPointEditUndoAction : EditorChangeTracker.IEditorAction
	{
		private PolygonCollider _collider;
		private List<Vector2> _oldPoints;
		private List<Vector2> _newPoints;
		private string _description;

		public string Description => _description;

		public PolygonColliderPointEditUndoAction(
			PolygonCollider collider,
			List<Vector2> oldPoints,
			List<Vector2> newPoints,
			string description)
		{
			_collider = collider;
			_oldPoints = new List<Vector2>(oldPoints);
			_newPoints = new List<Vector2>(newPoints);
			_description = description;
		}

		public void Undo()
		{
			if (_collider == null || _collider.Entity == null)
				return;

			_collider.Points.Clear();
			foreach (var point in _oldPoints)
			{
				_collider.Points.Add(point);
			}
			_collider.UpdateShapeFromPoints();
		}

		public void Redo()
		{
			if (_collider == null || _collider.Entity == null)
				return;

			_collider.Points.Clear();
			foreach (var point in _newPoints)
			{
				_collider.Points.Add(point);
			}
			_collider.UpdateShapeFromPoints();
		}
	}
}