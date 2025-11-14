using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez;
using Nez.ImGuiTools.UndoActions;
using Nez.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nez.ImGuiTools.Gizmos
{
	/// <summary>
	/// Handles gizmo rendering and interaction for entity position/translation using arrows
	/// </summary>
	public class EntityTransformGizmoHandler
	{
		private bool _draggingX = false;
		private bool _draggingY = false;
		private Vector2 _dragStartWorldMouse;
		private Dictionary<Entity, Vector2> _dragStartEntityPositions = new();
		private Dictionary<Entity, Vector2> _dragEndEntityPositions = new();

		public bool IsDragging => _draggingX || _draggingY;
		public bool IsMouseOverGizmo { get; private set; }

		/// <summary>
		/// Draws entity transform arrows and handles interaction
		/// </summary>
		public void Draw(List<Entity> selectedEntities, Vector2 worldMouse, Camera camera)
		{
			IsMouseOverGizmo = false;

			if (selectedEntities.Count == 0)
				return;

			// Calculate center, skipping entities with invalid positions
			Vector2 center = Vector2.Zero;
			int validEntityCount = 0;

			foreach (var e in selectedEntities)
			{
				if (MathUtils.IsVectorNaNOrInfinite(e.Transform.Position))
				{
					Debug.Warn($"Entity '{e.Name}' has invalid position: {e.Transform.Position}. Skipping from gizmo center calculation.");
					continue;
				}

				center += e.Transform.Position;
				validEntityCount++;
			}

			if (validEntityCount == 0)
			{
				Debug.Warn("No valid entities to draw gizmo for.");
				return;
			}

			center /= validEntityCount;

			float baseLength = 30f;
			float minLength = 10f;
			float maxLength = 100f;
			float axisLength = baseLength / MathF.Max(camera.RawZoom, 0.01f);
			axisLength = Math.Clamp(axisLength, minLength, maxLength);

			float baseWidth = 4f;
			float maxWidth = 16f;
			float scaledWidth = baseWidth;
			if (camera.RawZoom > 1f)
				scaledWidth = MathF.Min(baseWidth * camera.RawZoom, maxWidth);

			var screenPos = camera.WorldToScreenPoint(center);
			var axisEndX = camera.WorldToScreenPoint(center + new Vector2(axisLength, 0));
			var axisEndY = camera.WorldToScreenPoint(center + new Vector2(0, -axisLength));

			Color xColor = Color.Red;
			Color yColor = Color.LimeGreen;

			var mousePos = Input.ScaledMousePosition;

			bool xHovered = IsMouseNearLine(mousePos, screenPos, axisEndX);
			bool yHovered = IsMouseNearLine(mousePos, screenPos, axisEndY);

			IsMouseOverGizmo = xHovered || yHovered;

			if (_draggingX)
				xColor = Color.Yellow;
			else if (xHovered)
				xColor = Color.Orange;

			if (_draggingY)
				yColor = Color.Yellow;
			else if (yHovered)
				yColor = Color.Orange;

			Debug.DrawArrow(center, center + new Vector2(axisLength, 0), scaledWidth, scaledWidth, xColor);
			Debug.DrawArrow(center, center + new Vector2(0, -axisLength), scaledWidth, scaledWidth, yColor);

			HandleDragging(selectedEntities, worldMouse, camera, center, xHovered, yHovered);
		}

		private void HandleDragging(List<Entity> selectedEntities, Vector2 worldMouse, Camera camera, Vector2 center, bool xHovered, bool yHovered)
		{
			if (selectedEntities.Count == 0)
				return;

			var mousePos = Input.ScaledMousePosition;

			// Start dragging
			if (!_draggingX && !_draggingY)
			{
				if ((xHovered && yHovered && Input.LeftMouseButtonPressed) ||
					(xHovered && Input.LeftMouseButtonPressed) ||
					(yHovered && Input.LeftMouseButtonPressed))
				{
					if (xHovered && yHovered)
					{
						_draggingX = true;
						_draggingY = true;
					}
					else if (xHovered)
					{
						_draggingX = true;
					}
					else if (yHovered)
					{
						_draggingY = true;
					}

					_dragStartEntityPositions.Clear();
					foreach (var entity in selectedEntities)
					{
						if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
							continue;

						_dragStartEntityPositions[entity] = entity.Transform.Position;
					}

					_dragStartWorldMouse = camera.ScreenToWorldPoint(mousePos);
				}
			}

			// Dragging
			if ((_draggingX || _draggingY) && Input.LeftMouseButtonDown)
			{
				var delta = worldMouse - _dragStartWorldMouse;
				foreach (var entity in selectedEntities)
				{
					if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
						continue;

					var startPos = _dragStartEntityPositions.TryGetValue(entity, out var pos)
						? pos
						: entity.Transform.Position;

					if (_draggingX && _draggingY)
					{
						ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
						entity.Transform.Position = startPos + delta;
					}
					else if (_draggingX)
					{
						ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
						entity.Transform.Position = new Vector2(startPos.X + delta.X, startPos.Y);
					}
					else if (_draggingY)
					{
						ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
						entity.Transform.Position = new Vector2(startPos.X, startPos.Y + delta.Y);
					}
				}
			}

			// End drag
			if ((_draggingX || _draggingY) && Input.LeftMouseButtonReleased)
			{
				_draggingX = false;
				_draggingY = false;

				_dragEndEntityPositions = new Dictionary<Entity, Vector2>();
				foreach (var entity in selectedEntities)
				{
					if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
						continue;

					_dragEndEntityPositions[entity] = entity.Transform.Position;
				}

				// Only push undo if any entity moved
				bool anyMoved = selectedEntities.Any(e =>
					_dragStartEntityPositions.TryGetValue(e, out var startPos) &&
					_dragEndEntityPositions.TryGetValue(e, out var endPos) &&
					startPos != endPos
				);

				if (anyMoved)
				{
					EditorChangeTracker.PushUndo(
						new MultiEntityTransformUndoAction(
							selectedEntities.Where(e => !MathUtils.IsVectorNaNOrInfinite(e.Transform.Position)).ToList(),
							_dragStartEntityPositions,
							_dragEndEntityPositions,
							$"Moved {string.Join(", ", selectedEntities.Select(e => e.Name))}"
						),
						selectedEntities.First(e => !MathUtils.IsVectorNaNOrInfinite(e.Transform.Position)),
						$"Moved {string.Join(", ", selectedEntities.Select(e => e.Name))}"
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
			_draggingX = false;
			_draggingY = false;
			_dragStartEntityPositions.Clear();
			_dragEndEntityPositions.Clear();
		}
	}
}