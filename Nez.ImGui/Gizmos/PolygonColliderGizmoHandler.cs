using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez;
using Nez.Editor;
using Nez.ImGuiTools.UndoActions;
using Nez.PhysicsShapes;
using System.Collections.Generic;

namespace Nez.ImGuiTools.Gizmos
{
	/// <summary>
	/// Handles gizmo rendering and interaction for PolygonCollider components
	/// </summary>
	public class PolygonColliderGizmoHandler
	{
		private bool _isDragging = false;
		private PolygonCollider _selectedCollider;
		private int _selectedPointIndex = -1;
		private Dictionary<PolygonCollider, List<Vector2>> _originalPolygonPoints = new();

		public bool IsDragging => _isDragging;
		public bool IsMouseOverGizmo { get; private set; }

		/// <summary>
		/// Draws polygon collider gizmos and handles interaction
		/// </summary>
		public void Draw(List<PolygonCollider> polygonColliders, Vector2 worldMouse, Camera camera)
		{
			IsMouseOverGizmo = false;

			if (!_isDragging && Input.LeftMouseButtonPressed)
			{
				TryStartDragging(polygonColliders, worldMouse, camera);
			}

			if (_isDragging && Input.LeftMouseButtonDown)
			{
				UpdateDragging(worldMouse);
			}

			if (_isDragging && Input.LeftMouseButtonReleased)
			{
				EndDragging();
			}

			DrawPolygonPoints(polygonColliders, camera);
		}

		private void TryStartDragging(List<PolygonCollider> polygonColliders, Vector2 worldMouse, Camera camera)
		{
			float minDist = 10f / camera.RawZoom;
			PolygonCollider closestCollider = null;
			int closestPointIndex = -1;
			float closestDistance = float.MaxValue;

			foreach (var collider in polygonColliders)
			{
				var polygon = collider.Shape as Polygon;
				if (polygon == null || polygon.Points == null)
					continue;

				for (int i = 0; i < polygon.Points.Length; i++)
				{
					var worldPoint = collider.Entity.Transform.Position + collider.LocalOffset + polygon.Points[i];
					float dist = Vector2.Distance(worldMouse, worldPoint);

					if (dist < minDist && dist < closestDistance)
					{
						closestDistance = dist;
						closestCollider = collider;
						closestPointIndex = i;
					}
				}
			}

			if (closestCollider != null && closestPointIndex >= 0)
			{
				_isDragging = true;
				_selectedCollider = closestCollider;
				_selectedPointIndex = closestPointIndex;

				// Store original points for undo
				_originalPolygonPoints.Clear();
				foreach (var collider in polygonColliders)
				{
					_originalPolygonPoints[collider] = new List<Vector2>(collider.Points);
				}
			}
		}

		private void UpdateDragging(Vector2 worldMouse)
		{
			if (_selectedCollider != null && _selectedPointIndex >= 0)
			{
				var worldCenter = _selectedCollider.Entity.Transform.Position + _selectedCollider.LocalOffset;
				var newLocalPoint = worldMouse - worldCenter;

				_selectedCollider.Points[_selectedPointIndex] = newLocalPoint;
				_selectedCollider.UpdateShapeFromPoints();

				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
				IsMouseOverGizmo = true;
			}
		}

		private void EndDragging()
		{
			if (_selectedCollider != null)
			{
				var finalPoints = new Dictionary<PolygonCollider, List<Vector2>>
				{
					[_selectedCollider] = new List<Vector2>(_selectedCollider.Points)
				};

				EditorChangeTracker.PushUndo(
					new PolygonColliderPointEditUndoAction(
						_selectedCollider,
						_originalPolygonPoints[_selectedCollider],
						finalPoints[_selectedCollider],
						$"Edited PolygonCollider point on {_selectedCollider.Entity.Name}"
					),
					_selectedCollider.Entity,
					$"Edited PolygonCollider point"
				);
			}

			_isDragging = false;
			_selectedCollider = null;
			_selectedPointIndex = -1;
			_originalPolygonPoints.Clear();
		}

		private void DrawPolygonPoints(List<PolygonCollider> polygonColliders, Camera camera)
		{
			float pointSize = 6f / camera.RawZoom;

			foreach (var collider in polygonColliders)
			{
				var polygon = collider.Shape as Polygon;
				if (polygon == null || polygon.Points == null)
					continue;

				for (int i = 0; i < polygon.Points.Length; i++)
				{
					var worldPoint = collider.Entity.Transform.Position + collider.LocalOffset + polygon.Points[i];

					bool isSelected = _isDragging &&
					                  collider == _selectedCollider &&
					                  i == _selectedPointIndex;

					var pointColor = isSelected ? Color.Yellow : Color.Cyan;

					Debug.DrawHollowRect(new RectangleF(
						worldPoint.X - pointSize / 2f,
						worldPoint.Y - pointSize / 2f,
						pointSize,
						pointSize
					), pointColor);

					// Draw lines between points
					if (i < polygon.Points.Length - 1)
					{
						var nextWorldPoint = collider.Entity.Transform.Position + collider.LocalOffset + polygon.Points[i + 1];
						Debug.DrawLine(worldPoint, nextWorldPoint, Color.Cyan * 0.5f);
					}
					else
					{
						// Connect last point to first
						var firstWorldPoint = collider.Entity.Transform.Position + collider.LocalOffset + polygon.Points[0];
						Debug.DrawLine(worldPoint, firstWorldPoint, Color.Cyan * 0.5f);
					}
				}

				var centerPoint = collider.Entity.Transform.Position + collider.LocalOffset;
				Debug.DrawPixel(centerPoint, 4 * Debug.Size.LineSizeMultiplier, new Color(200, 0, 255));
			}
		}

		/// <summary>
		/// Resets the dragging state
		/// </summary>
		public void Reset()
		{
			_isDragging = false;
			_selectedCollider = null;
			_selectedPointIndex = -1;
			_originalPolygonPoints.Clear();
		}
	}
}