using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez;
using Nez.ImGuiTools.UndoActions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nez.ImGuiTools.Gizmos
{
	/// <summary>
	/// Handles gizmo rendering and interaction for entity scale using rectangles at axis ends
	/// </summary>
	public class EntityScaleGizmoHandler
	{
		private Dictionary<Entity, Vector2> _dragStartEntityScales = new();
		private Dictionary<Entity, Vector2> _dragEndEntityScales = new();
		private Vector2 _dragStartScaleMouse;
		private bool _draggingScaleX = false;
		private bool _draggingScaleY = false;

		public bool IsDragging => _draggingScaleX || _draggingScaleY;
		public bool IsMouseOverGizmo { get; private set; }

		/// <summary>
		/// Draws entity scale gizmo and handles interaction
		/// </summary>
		public void Draw(List<Entity> selectedEntities, Vector2 worldMouse, Camera camera)
		{
			IsMouseOverGizmo = false;

			if (selectedEntities.Count == 0)
				return;

			// Compute center of all selected entities
			Vector2 center = Vector2.Zero;
			foreach (var e in selectedEntities)
				center += e.Transform.Position;
			center /= selectedEntities.Count;

			float baseLength = 30f;
			float minLength = 10f;
			float maxLength = 100f;
			float axisLength = baseLength / MathF.Max(camera.RawZoom, 0.01f);
			axisLength = Math.Clamp(axisLength, minLength, maxLength);

			var screenPos = camera.WorldToScreenPoint(center);

			// If dragging, move only the selected axis's end to follow the mouse position instantly
			Vector2 axisEndX, axisEndY;
			var mousePos = Input.ScaledMousePosition;

			bool isDragging = _draggingScaleX || _draggingScaleY;

			if (isDragging)
			{
				// X axis follows mouse X only if dragging X, otherwise stays at default length
				if (_draggingScaleX)
					axisEndX = camera.WorldToScreenPoint(new Vector2(worldMouse.X, center.Y));
				else
					axisEndX = camera.WorldToScreenPoint(center + new Vector2(axisLength, 0));

				// Same as X
				if (_draggingScaleY)
					axisEndY = camera.WorldToScreenPoint(new Vector2(center.X, worldMouse.Y));
				else
					axisEndY = camera.WorldToScreenPoint(center + new Vector2(0, -axisLength));
			}
			else
			{
				axisEndX = camera.WorldToScreenPoint(center + new Vector2(axisLength, 0));
				axisEndY = camera.WorldToScreenPoint(center + new Vector2(0, -axisLength));
			}

			Color xColor = Color.DeepSkyBlue;
			Color yColor = Color.MediumVioletRed;

			bool xHovered = IsMouseNearLine(mousePos, screenPos, axisEndX);
			bool yHovered = IsMouseNearLine(mousePos, screenPos, axisEndY);

			Debug.DrawLine(center, camera.ScreenToWorldPoint(axisEndX), xColor);
			Debug.DrawLine(center, camera.ScreenToWorldPoint(axisEndY), yColor);

			// Draw rectangles at the end of each axis
			float rectSize = 8f / camera.RawZoom;
			Vector2 rectOrigin = new Vector2(rectSize / 2f, rectSize / 2f);
			Debug.DrawRect(new RectangleF(camera.ScreenToWorldPoint(axisEndX).X - rectOrigin.X, camera.ScreenToWorldPoint(axisEndX).Y - rectOrigin.Y, rectSize, rectSize), xHovered ? Color.Orange : xColor, 0f);
			Debug.DrawRect(new RectangleF(camera.ScreenToWorldPoint(axisEndY).X - rectOrigin.X, camera.ScreenToWorldPoint(axisEndY).Y - rectOrigin.Y, rectSize, rectSize), yHovered ? Color.Orange : yColor, 0f);

			IsMouseOverGizmo = xHovered || yHovered;

			HandleScaleDragging(selectedEntities, worldMouse, camera, center, xHovered, yHovered);
		}

		private void HandleScaleDragging(List<Entity> selectedEntities, Vector2 worldMouse, Camera camera, Vector2 center, bool xHovered, bool yHovered)
		{
			if (selectedEntities.Count == 0)
				return;

			var mousePos = Input.ScaledMousePosition;

			// Start dragging
			if (!_draggingScaleX && !_draggingScaleY)
			{
				if ((xHovered && yHovered && Input.LeftMouseButtonPressed) ||
					(xHovered && Input.LeftMouseButtonPressed) ||
					(yHovered && Input.LeftMouseButtonPressed))
				{
					if (xHovered && yHovered)
					{
						_draggingScaleX = true;
						_draggingScaleY = true;
					}
					else if (xHovered)
					{
						_draggingScaleX = true;
					}
					else if (yHovered)
					{
						_draggingScaleY = true;
					}

					_dragStartEntityScales.Clear();
					foreach (var entity in selectedEntities)
						_dragStartEntityScales[entity] = entity.Transform.Scale;

					_dragStartScaleMouse = camera.ScreenToWorldPoint(mousePos);
				}
			}

			// Dragging
			if ((_draggingScaleX || _draggingScaleY) && Input.LeftMouseButtonDown)
			{
				var delta = (worldMouse - _dragStartScaleMouse) / 10f; // 10x slower scaling

				// Reverse Y scaling direction only for entity scaling
				var entityDelta = new Vector2(delta.X, -delta.Y);

				foreach (var entity in selectedEntities)
				{
					var startScale = _dragStartEntityScales.TryGetValue(entity, out var scale)
						? scale
						: entity.Transform.Scale;
					Vector2 newScale = startScale;

					if (_draggingScaleX && _draggingScaleY)
					{
						ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
						newScale = startScale + entityDelta;
					}
					else if (_draggingScaleX)
					{
						ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
						newScale = new Vector2(startScale.X + entityDelta.X, startScale.Y);
					}
					else if (_draggingScaleY)
					{
						ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
						newScale = new Vector2(startScale.X, startScale.Y + entityDelta.Y);
					}

					// Prevent negative or zero scale
					newScale.X = MathF.Max(newScale.X, 0.01f);
					newScale.Y = MathF.Max(newScale.Y, 0.01f);

					entity.Transform.Scale = newScale;
				}
			}

			// End drag
			if ((_draggingScaleX || _draggingScaleY) && Input.LeftMouseButtonReleased)
			{
				_draggingScaleX = false;
				_draggingScaleY = false;

				_dragEndEntityScales = new Dictionary<Entity, Vector2>();
				foreach (var entity in selectedEntities)
					_dragEndEntityScales[entity] = entity.Transform.Scale;

				// Only push undo if any entity scaled
				bool anyScaled = selectedEntities.Any(e =>
					_dragStartEntityScales.TryGetValue(e, out var startScale) &&
					_dragEndEntityScales.TryGetValue(e, out var endScale) &&
					startScale != endScale
				);

				if (anyScaled)
				{
					EditorChangeTracker.PushUndo(
						new MultiEntityScaleUndoAction(
							selectedEntities.ToList(),
							_dragStartEntityScales,
							_dragEndEntityScales,
							$"Scaled {string.Join(", ", selectedEntities.Select(e => e.Name))}"
						),
						selectedEntities.First(),
						$"Scaled {string.Join(", ", selectedEntities.Select(e => e.Name))}"
					);
				}
			}
		}

		private bool IsMouseNearLine(Vector2 mouse, Vector2 a, Vector2 b, float threshold = 10f)
		{
			var ap = mouse - a;
			var ab = b - a;
			float abLen = ab.Length();
			float t = Math.Clamp(Vector2.Dot(ap, ab) / (abLen * abLen), 0, 1);
			var closest = a + ab * t;
			return (mouse - closest).Length() < threshold;
		}

		/// <summary>
		/// Resets the dragging state
		/// </summary>
		public void Reset()
		{
			_draggingScaleX = false;
			_draggingScaleY = false;
			_dragStartEntityScales.Clear();
			_dragEndEntityScales.Clear();
		}
	}
}