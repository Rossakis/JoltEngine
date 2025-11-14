using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez;
using Nez.DeferredLighting;
using Nez.Editor;
using Nez.ImGuiTools.UndoActions;
using Nez.PhysicsShapes;
using System;
using System.Collections.Generic;

namespace Nez.ImGuiTools.Gizmos
{
	/// <summary>
	/// Handle types for rectangle manipulation
	/// </summary>
	public enum RectangleHandleType
	{
		None,
		TopLeft,
		TopRight,
		BottomLeft,
		BottomRight,
		Top,
		Bottom,
		Left,
		Right
	}

	/// <summary>
	/// Handles gizmo rendering and interaction for BoxCollider and AreaLight components
	/// </summary>
	public class RectangleGizmoHandler
	{
		private bool _isDragging = false;
		private BoxCollider _selectedBoxCollider;
		private AreaLight _selectedAreaLight;
		private RectangleHandleType _selectedHandleType;
		private Dictionary<BoxCollider, RectangleF> _originalBoxBounds = new();
		private Dictionary<AreaLight, RectangleF> _originalAreaLightBounds = new();
		private bool _shiftDown;

		public bool IsDragging => _isDragging;
		public bool IsMouseOverGizmo { get; private set; }

		/// <summary>
		/// Draws rectangle gizmos for BoxColliders and AreaLights
		/// </summary>
		public void Draw(List<BoxCollider> boxColliders, List<AreaLight> areaLights, Vector2 worldMouse, Camera camera, bool shiftDown)
		{
			IsMouseOverGizmo = false;
			_shiftDown = shiftDown;

			float handleSize = 6f / camera.RawZoom;
			float edgeThreshold = 8f / camera.RawZoom;

			if (!_isDragging && Input.LeftMouseButtonPressed)
			{
				TryStartDragging(boxColliders, areaLights, worldMouse, handleSize, edgeThreshold);
			}

			if (_isDragging && Input.LeftMouseButtonDown)
			{
				UpdateDragging(worldMouse);
			}

			if (_isDragging && Input.LeftMouseButtonReleased)
			{
				EndDragging();
			}

			DrawHandles(boxColliders, areaLights, handleSize, camera);
		}

		private void TryStartDragging(List<BoxCollider> boxColliders, List<AreaLight> areaLights, Vector2 worldMouse, float handleSize, float edgeThreshold)
		{
			BoxCollider closestCollider = null;
			AreaLight closestAreaLight = null;
			RectangleHandleType closestHandle = RectangleHandleType.None;
			float closestDistance = float.MaxValue;

			// BoxColliders
			foreach (var collider in boxColliders)
			{
				var bounds = GetBoxColliderWorldBounds(collider);
				CheckHandlesForBounds(bounds, worldMouse, handleSize, edgeThreshold,
					ref closestDistance, ref closestHandle,
					() => { closestCollider = collider; closestAreaLight = null; });
			}

			// AreaLights
			foreach (var areaLight in areaLights)
			{
				var bounds = GetAreaLightWorldBounds(areaLight);
				CheckHandlesForBounds(bounds, worldMouse, handleSize, edgeThreshold,
					ref closestDistance, ref closestHandle,
					() => { closestAreaLight = areaLight; closestCollider = null; });
			}

			if ((closestCollider != null || closestAreaLight != null) && closestHandle != RectangleHandleType.None)
			{
				_isDragging = true;
				_selectedBoxCollider = closestCollider;
				_selectedAreaLight = closestAreaLight;
				_selectedHandleType = closestHandle;

				// Store original bounds for undo
				_originalBoxBounds.Clear();
				_originalAreaLightBounds.Clear();

				foreach (var collider in boxColliders)
				{
					_originalBoxBounds[collider] = GetBoxColliderWorldBounds(collider);
				}

				foreach (var areaLight in areaLights)
				{
					_originalAreaLightBounds[areaLight] = GetAreaLightWorldBounds(areaLight);
				}
			}
		}

		private void UpdateDragging(Vector2 worldMouse)
		{
			if (_selectedBoxCollider != null)
			{
				ResizeBoxCollider(_selectedBoxCollider, worldMouse, _selectedHandleType, _shiftDown);
			}
			else if (_selectedAreaLight != null)
			{
				ResizeAreaLight(_selectedAreaLight, worldMouse, _selectedHandleType, _shiftDown);
			}

			IsMouseOverGizmo = true;

			// Set appropriate cursor based on handle type
			switch (_selectedHandleType)
			{
				case RectangleHandleType.TopLeft:
				case RectangleHandleType.BottomRight:
					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
					break;
				case RectangleHandleType.TopRight:
				case RectangleHandleType.BottomLeft:
					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNESW);
					break;
				case RectangleHandleType.Top:
				case RectangleHandleType.Bottom:
					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
					break;
				case RectangleHandleType.Left:
				case RectangleHandleType.Right:
					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
					break;
			}
		}

		private void EndDragging()
		{
			if (_selectedBoxCollider != null)
			{
				var finalBounds = GetBoxColliderWorldBounds(_selectedBoxCollider);

				EditorChangeTracker.PushUndo(
					new BoxColliderResizeUndoAction(
						_selectedBoxCollider,
						_originalBoxBounds[_selectedBoxCollider],
						finalBounds,
						$"Resized BoxCollider on {_selectedBoxCollider.Entity.Name}"
					),
					_selectedBoxCollider.Entity,
					$"Resized BoxCollider"
				);
			}
			else if (_selectedAreaLight != null)
			{
				var finalBounds = GetAreaLightWorldBounds(_selectedAreaLight);

				EditorChangeTracker.PushUndo(
					new AreaLightResizeUndoAction(
						_selectedAreaLight,
						_originalAreaLightBounds[_selectedAreaLight],
						finalBounds,
						$"Resized AreaLight on {_selectedAreaLight.Entity.Name}"
					),
					_selectedAreaLight.Entity,
					$"Resized AreaLight"
				);
			}

			_isDragging = false;
			_selectedBoxCollider = null;
			_selectedAreaLight = null;
			_selectedHandleType = RectangleHandleType.None;
			_originalBoxBounds.Clear();
			_originalAreaLightBounds.Clear();
		}

		private void DrawHandles(List<BoxCollider> boxColliders, List<AreaLight> areaLights, float handleSize, Camera camera)
		{
			// Draw handles for BoxColliders
			foreach (var collider in boxColliders)
			{
				var bounds = GetBoxColliderWorldBounds(collider);
				DrawRectangleHandles(bounds, handleSize, Color.Magenta,
					_isDragging && collider == _selectedBoxCollider, camera);
			}

			// Draw handles for AreaLights
			foreach (var areaLight in areaLights)
			{
				var bounds = GetAreaLightWorldBounds(areaLight);
				DrawRectangleHandles(bounds, handleSize, Color.Yellow,
					_isDragging && areaLight == _selectedAreaLight, camera);
			}
		}

		private void CheckHandlesForBounds(RectangleF bounds, Vector2 worldMouse, float handleSize, float edgeThreshold,
			ref float closestDistance, ref RectangleHandleType closestHandle,
			Action setTarget)
		{
			// Calculate corner positions
			var topLeft = new Vector2(bounds.Left, bounds.Top);
			var topRight = new Vector2(bounds.Right, bounds.Top);
			var bottomLeft = new Vector2(bounds.Left, bounds.Bottom);
			var bottomRight = new Vector2(bounds.Right, bounds.Bottom);

			var corners = new[]
			{
				(topLeft, RectangleHandleType.TopLeft),
				(topRight, RectangleHandleType.TopRight),
				(bottomLeft, RectangleHandleType.BottomLeft),
				(bottomRight, RectangleHandleType.BottomRight)
			};

			foreach (var (corner, handleType) in corners)
			{
				float dist = Vector2.Distance(worldMouse, corner);
				if (dist < handleSize && dist < closestDistance)
				{
					closestDistance = dist;
					setTarget();
					closestHandle = handleType;
				}
			}

			// If no corner selected, check edges
			if (closestHandle == RectangleHandleType.None)
			{
				// Top edge
				if (Math.Abs(worldMouse.Y - bounds.Top) < edgeThreshold &&
				    worldMouse.X >= bounds.Left && worldMouse.X <= bounds.Right)
				{
					float dist = Math.Abs(worldMouse.Y - bounds.Top);
					if (dist < closestDistance)
					{
						closestDistance = dist;
						setTarget();
						closestHandle = RectangleHandleType.Top;
					}
				}

				// Bottom edge
				if (Math.Abs(worldMouse.Y - bounds.Bottom) < edgeThreshold &&
				    worldMouse.X >= bounds.Left && worldMouse.X <= bounds.Right)
				{
					float dist = Math.Abs(worldMouse.Y - bounds.Bottom);
					if (dist < closestDistance)
					{
						closestDistance = dist;
						setTarget();
						closestHandle = RectangleHandleType.Bottom;
					}
				}

				// Left edge
				if (Math.Abs(worldMouse.X - bounds.Left) < edgeThreshold &&
				    worldMouse.Y >= bounds.Top && worldMouse.Y <= bounds.Bottom)
				{
					float dist = Math.Abs(worldMouse.X - bounds.Left);
					if (dist < closestDistance)
					{
						closestDistance = dist;
						setTarget();
						closestHandle = RectangleHandleType.Left;
					}
				}

				// Right edge
				if (Math.Abs(worldMouse.X - bounds.Right) < edgeThreshold &&
				    worldMouse.Y >= bounds.Top && worldMouse.Y <= bounds.Bottom)
				{
					float dist = Math.Abs(worldMouse.X - bounds.Right);
					if (dist < closestDistance)
					{
						closestDistance = dist;
						setTarget();
						closestHandle = RectangleHandleType.Right;
					}
				}
			}
		}

		private void DrawRectangleHandles(RectangleF bounds, float handleSize, Color baseColor, bool isSelected, Camera camera)
		{
			var corners = new[]
			{
				(new Vector2(bounds.Left, bounds.Top), RectangleHandleType.TopLeft),
				(new Vector2(bounds.Right, bounds.Top), RectangleHandleType.TopRight),
				(new Vector2(bounds.Left, bounds.Bottom), RectangleHandleType.BottomLeft),
				(new Vector2(bounds.Right, bounds.Bottom), RectangleHandleType.BottomRight)
			};

			foreach (var (corner, handleType) in corners)
			{
				bool isThisHandleSelected = isSelected && _selectedHandleType == handleType;
				var pointColor = isThisHandleSelected ? Color.Yellow : baseColor;

				Debug.DrawHollowRect(new RectangleF(
					corner.X - handleSize / 2f,
					corner.Y - handleSize / 2f,
					handleSize,
					handleSize
				), pointColor);
			}

			Debug.DrawHollowRect(bounds, baseColor * 0.5f);

			var center = new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
			Debug.DrawPixel(center, 4 * Debug.Size.LineSizeMultiplier, new Color(200, 0, 255));
		}

		#region BoxCollider Resize Logic

		private void ResizeBoxCollider(BoxCollider collider, Vector2 worldMouse, RectangleHandleType handleType, bool mirroredScaling)
		{
			var box = collider.Shape as Box;
			if (box == null)
				return;

			if (mirroredScaling)
			{
				ResizeBoxColliderMirrored(collider, worldMouse, handleType);
			}
			else
			{
				ResizeBoxColliderVertex(collider, worldMouse, handleType);
			}
		}

		private void ResizeBoxColliderMirrored(BoxCollider collider, Vector2 worldMouse, RectangleHandleType handleType)
		{
			var bounds = collider.Bounds;
			var box = collider.Shape as Box;

			var originalCenter = new Vector2(
				bounds.X + bounds.Width * 0.5f,
				bounds.Y + bounds.Height * 0.5f
			);

			var newLeft = bounds.Left;
			var newTop = bounds.Top;
			var newRight = bounds.Right;
			var newBottom = bounds.Bottom;

			switch (handleType)
			{
				case RectangleHandleType.TopLeft:
					var topLeftDelta = worldMouse - new Vector2(bounds.Left, bounds.Top);
					newLeft = worldMouse.X;
					newTop = worldMouse.Y;
					newRight = bounds.Right - topLeftDelta.X;
					newBottom = bounds.Bottom - topLeftDelta.Y;
					break;

				case RectangleHandleType.TopRight:
					var topRightDelta = worldMouse - new Vector2(bounds.Right, bounds.Top);
					newRight = worldMouse.X;
					newTop = worldMouse.Y;
					newLeft = bounds.Left - topRightDelta.X;
					newBottom = bounds.Bottom - topRightDelta.Y;
					break;

				case RectangleHandleType.BottomLeft:
					var bottomLeftDelta = worldMouse - new Vector2(bounds.Left, bounds.Bottom);
					newLeft = worldMouse.X;
					newBottom = worldMouse.Y;
					newRight = bounds.Right - bottomLeftDelta.X;
					newTop = bounds.Top - bottomLeftDelta.Y;
					break;

				case RectangleHandleType.BottomRight:
					var bottomRightDelta = worldMouse - new Vector2(bounds.Right, bounds.Bottom);
					newRight = worldMouse.X;
					newBottom = worldMouse.Y;
					newLeft = bounds.Left - bottomRightDelta.X;
					newTop = bounds.Top - bottomRightDelta.Y;
					break;

				case RectangleHandleType.Top:
					var topDelta = worldMouse.Y - bounds.Top;
					newTop = worldMouse.Y;
					newBottom = bounds.Bottom - topDelta;
					break;

				case RectangleHandleType.Bottom:
					var bottomDelta = worldMouse.Y - bounds.Bottom;
					newBottom = worldMouse.Y;
					newTop = bounds.Top - bottomDelta;
					break;

				case RectangleHandleType.Left:
					var leftDelta = worldMouse.X - bounds.Left;
					newLeft = worldMouse.X;
					newRight = bounds.Right - leftDelta;
					break;

				case RectangleHandleType.Right:
					var rightDelta = worldMouse.X - bounds.Right;
					newRight = worldMouse.X;
					newLeft = bounds.Left - rightDelta;
					break;
			}

			const float minSize = 1f;
			var newWidth = Math.Max(newRight - newLeft, minSize);
			var newHeight = Math.Max(newBottom - newTop, minSize);

			box.UpdateBox(newWidth, newHeight);

			var entityPos = collider.Entity.Transform.Position;
			collider.LocalOffset = new Vector2(originalCenter.X - entityPos.X, originalCenter.Y - entityPos.Y);

			if (collider.Entity != null && collider.Enabled)
				Physics.UpdateCollider(collider);
		}

		private void ResizeBoxColliderVertex(BoxCollider collider, Vector2 worldMouse, RectangleHandleType handleType)
		{
			var box = collider.Shape as Box;
			var entityPos = collider.Entity.Transform.Position;

			// Store the original world-space center before modification
			var originalBounds = GetBoxColliderWorldBounds(collider);
			var originalWorldCenter = new Vector2(
				originalBounds.X + originalBounds.Width / 2f,
				originalBounds.Y + originalBounds.Height / 2f
			);

			var localToCollider = worldMouse - entityPos - collider.LocalOffset;
			var points = box.Points;

			// Store the fixed corner in world space BEFORE modification
			Vector2 fixedCornerWorld = Vector2.Zero;
			float fixedEdgeWorld = 0f;
			bool isEdgeDrag = false;

			switch (handleType)
			{
				case RectangleHandleType.TopLeft:
					// Fixed corner is bottom-right (point 2)
					fixedCornerWorld = entityPos + collider.LocalOffset + points[2];
					points[0] = new Vector2(localToCollider.X, localToCollider.Y);
					points[1] = new Vector2(points[1].X, localToCollider.Y);
					points[3] = new Vector2(localToCollider.X, points[3].Y);
					break;

				case RectangleHandleType.TopRight:
					// Fixed corner is bottom-left (point 3)
					fixedCornerWorld = entityPos + collider.LocalOffset + points[3];
					points[1] = new Vector2(localToCollider.X, localToCollider.Y);
					points[0] = new Vector2(points[0].X, localToCollider.Y);
					points[2] = new Vector2(localToCollider.X, points[2].Y);
					break;

				case RectangleHandleType.BottomRight:
					// Fixed corner is top-left (point 0)
					fixedCornerWorld = entityPos + collider.LocalOffset + points[0];
					points[2] = new Vector2(localToCollider.X, localToCollider.Y);
					points[1] = new Vector2(localToCollider.X, points[1].Y);
					points[3] = new Vector2(points[3].X, localToCollider.Y);
					break;

				case RectangleHandleType.BottomLeft:
					// Fixed corner is top-right (point 1)
					fixedCornerWorld = entityPos + collider.LocalOffset + points[1];
					points[3] = new Vector2(localToCollider.X, localToCollider.Y);
					points[0] = new Vector2(localToCollider.X, points[0].Y);
					points[2] = new Vector2(points[2].X, localToCollider.Y);
					break;

				case RectangleHandleType.Top:
					isEdgeDrag = true;
					// Keep bottom edge fixed
					fixedEdgeWorld = originalBounds.Bottom;
					points[0] = new Vector2(points[0].X, localToCollider.Y);
					points[1] = new Vector2(points[1].X, localToCollider.Y);
					break;

				case RectangleHandleType.Bottom:
					isEdgeDrag = true;
					// Keep top edge fixed
					fixedEdgeWorld = originalBounds.Top;
					points[2] = new Vector2(points[2].X, localToCollider.Y);
					points[3] = new Vector2(points[3].X, localToCollider.Y);
					break;

				case RectangleHandleType.Left:
					isEdgeDrag = true;
					// Keep right edge fixed
					fixedEdgeWorld = originalBounds.Right;
					points[0] = new Vector2(localToCollider.X, points[0].Y);
					points[3] = new Vector2(localToCollider.X, points[3].Y);
					break;

				case RectangleHandleType.Right:
					isEdgeDrag = true;
					// Keep left edge fixed
					fixedEdgeWorld = originalBounds.Left;
					points[1] = new Vector2(localToCollider.X, points[1].Y);
					points[2] = new Vector2(localToCollider.X, points[2].Y);
					break;
			}

			for (var i = 0; i < points.Length; i++)
				box.OriginalPoints[i] = points[i];

			var minX = float.MaxValue;
			var minY = float.MaxValue;
			var maxX = float.MinValue;
			var maxY = float.MinValue;

			foreach (var p in points)
			{
				if (p.X < minX) minX = p.X;
				if (p.Y < minY) minY = p.Y;
				if (p.X > maxX) maxX = p.X;
				if (p.Y > maxY) maxY = p.Y;
			}

			box.Width = maxX - minX;
			box.Height = maxY - minY;

			const float minSize = 1f;
			if (box.Width < minSize || box.Height < minSize)
				return;

			var newLocalCenter = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);

			// Update LocalOffset based on whether we're dragging an edge or a corner
			if (isEdgeDrag)
			{
				// For edge editing, keep the opposite edge at the same world position
				switch (handleType)
				{
					case RectangleHandleType.Top:
					case RectangleHandleType.Bottom:
						// Keep the horizontal center the same, adjust vertical position to keep fixed edge in place
						var verticalCenter = (handleType == RectangleHandleType.Top)
							? fixedEdgeWorld - box.Height / 2f
							: fixedEdgeWorld + box.Height / 2f;
						collider.LocalOffset = new Vector2(
							originalWorldCenter.X - entityPos.X - newLocalCenter.X,
							verticalCenter - entityPos.Y - newLocalCenter.Y
						);
						break;

					case RectangleHandleType.Left:
					case RectangleHandleType.Right:
						// Keep the vertical center the same, adjust horizontal position to keep fixed edge in place
						var horizontalCenter = (handleType == RectangleHandleType.Left)
							? fixedEdgeWorld - box.Width / 2f
							: fixedEdgeWorld + box.Width / 2f;
						collider.LocalOffset = new Vector2(
							horizontalCenter - entityPos.X - newLocalCenter.X,
							originalWorldCenter.Y - entityPos.Y - newLocalCenter.Y
						);
						break;
				}
			}
			else
			{
				// The world position of the center should be halfway between fixed corner and moved corner
				var movedCornerWorld = worldMouse;
				var desiredWorldCenter = (fixedCornerWorld + movedCornerWorld) / 2f;

				// Set LocalOffset to place the box's local center at the desired world position
				collider.LocalOffset = new Vector2(
					desiredWorldCenter.X - entityPos.X - newLocalCenter.X,
					desiredWorldCenter.Y - entityPos.Y - newLocalCenter.Y
				);
			}

			box.UpdateBox(collider.Bounds.Width, collider.Bounds.Height);

			// Add the proper offset to the Collider
			var originalCenter = new Vector2(
				collider.Bounds.X + collider.Bounds.Width * 0.5f,
				collider.Bounds.Y + collider.Bounds.Height * 0.5f
			);

			collider.LocalOffset = new Vector2(originalCenter.X - entityPos.X, originalCenter.Y - entityPos.Y);

			if (collider.Entity != null && collider.Enabled)
				Physics.UpdateCollider(collider);
		}

		private RectangleF GetBoxColliderWorldBounds(BoxCollider collider)
		{
			var box = collider.Shape as Box;
			if (box == null || box.Points == null || box.Points.Length == 0)
				return collider.Bounds;

			var entityPos = collider.Entity.Transform.Position;
			var localOffset = collider.LocalOffset;

			var minX = float.MaxValue;
			var minY = float.MaxValue;
			var maxX = float.MinValue;
			var maxY = float.MinValue;

			foreach (var point in box.Points)
			{
				var worldPoint = entityPos + localOffset + point;

				if (worldPoint.X < minX) minX = worldPoint.X;
				if (worldPoint.Y < minY) minY = worldPoint.Y;
				if (worldPoint.X > maxX) maxX = worldPoint.X;
				if (worldPoint.Y > maxY) maxY = worldPoint.Y;
			}

			return new RectangleF(minX, minY, maxX - minX, maxY - minY);
		}

		#endregion

		#region AreaLight Resize Logic

		private void ResizeAreaLight(AreaLight areaLight, Vector2 worldMouse, RectangleHandleType handleType, bool mirroredScaling)
		{
			if (mirroredScaling)
			{
				ResizeAreaLightMirrored(areaLight, worldMouse, handleType);
			}
			else
			{
				ResizeAreaLightVertex(areaLight, worldMouse, handleType);
			}
		}

		private void ResizeAreaLightMirrored(AreaLight areaLight, Vector2 worldMouse, RectangleHandleType handleType)
		{
			var bounds = areaLight.Bounds;
			var scale = areaLight.Entity.Transform.Scale;

			var newLeft = bounds.Left;
			var newTop = bounds.Top;
			var newRight = bounds.Right;
			var newBottom = bounds.Bottom;

			switch (handleType)
			{
				case RectangleHandleType.TopLeft:
					var topLeftDelta = worldMouse - new Vector2(bounds.Left, bounds.Top);
					newLeft = worldMouse.X;
					newTop = worldMouse.Y;
					newRight = bounds.Right - topLeftDelta.X;
					newBottom = bounds.Bottom - topLeftDelta.Y;
					break;

				case RectangleHandleType.TopRight:
					var topRightDelta = worldMouse - new Vector2(bounds.Right, bounds.Top);
					newRight = worldMouse.X;
					newTop = worldMouse.Y;
					newLeft = bounds.Left - topRightDelta.X;
					newBottom = bounds.Bottom - topRightDelta.Y;
					break;

				case RectangleHandleType.BottomLeft:
					var bottomLeftDelta = worldMouse - new Vector2(bounds.Left, bounds.Bottom);
					newLeft = worldMouse.X;
					newBottom = worldMouse.Y;
					newRight = bounds.Right - bottomLeftDelta.X;
					newTop = bounds.Top - bottomLeftDelta.Y;
					break;

				case RectangleHandleType.BottomRight:
					var bottomRightDelta = worldMouse - new Vector2(bounds.Right, bounds.Bottom);
					newRight = worldMouse.X;
					newBottom = worldMouse.Y;
					newLeft = bounds.Left - bottomRightDelta.X;
					newTop = bounds.Top - bottomRightDelta.Y;
					break;

				case RectangleHandleType.Top:
					var topDelta = worldMouse.Y - bounds.Top;
					newTop = worldMouse.Y;
					newBottom = bounds.Bottom - topDelta;
					break;

				case RectangleHandleType.Bottom:
					var bottomDelta = worldMouse.Y - bounds.Bottom;
					newBottom = worldMouse.Y;
					newTop = bounds.Top - bottomDelta;
					break;

				case RectangleHandleType.Left:
					var leftDelta = worldMouse.X - bounds.Left;
					newLeft = worldMouse.X;
					newRight = bounds.Right - leftDelta;
					break;

				case RectangleHandleType.Right:
					var rightDelta = worldMouse.X - bounds.Right;
					newRight = worldMouse.X;
					newLeft = bounds.Left - rightDelta;
					break;
			}

			const float minSize = 1f;
			var newWidth = Math.Max(newRight - newLeft, minSize) / scale.X;
			var newHeight = Math.Max(newBottom - newTop, minSize) / scale.Y;

			areaLight.SetWidth(newWidth);
			areaLight.SetHeight(newHeight);
		}

		private void ResizeAreaLightVertex(AreaLight areaLight, Vector2 worldMouse, RectangleHandleType handleType)
		{
			var entityPos = areaLight.Entity.Transform.Position;
			var scale = areaLight.Entity.Transform.Scale;
			var bounds = areaLight.Bounds;

			var topLeft = new Vector2(bounds.Left, bounds.Top);
			var topRight = new Vector2(bounds.Right, bounds.Top);
			var bottomLeft = new Vector2(bounds.Left, bounds.Bottom);
			var bottomRight = new Vector2(bounds.Right, bounds.Bottom);

			Vector2 fixedCorner;
			float newWidth, newHeight;

			switch (handleType)
			{
				case RectangleHandleType.TopLeft:
					fixedCorner = bottomRight;
					newWidth = Math.Abs(fixedCorner.X - worldMouse.X) / scale.X;
					newHeight = Math.Abs(fixedCorner.Y - worldMouse.Y) / scale.Y;
					break;

				case RectangleHandleType.TopRight:
					fixedCorner = bottomLeft;
					newWidth = Math.Abs(worldMouse.X - fixedCorner.X) / scale.X;
					newHeight = Math.Abs(fixedCorner.Y - worldMouse.Y) / scale.Y;
					break;

				case RectangleHandleType.BottomRight:
					fixedCorner = topLeft;
					newWidth = Math.Abs(worldMouse.X - fixedCorner.X) / scale.X;
					newHeight = Math.Abs(worldMouse.Y - fixedCorner.Y) / scale.Y;
					break;

				case RectangleHandleType.BottomLeft:
					fixedCorner = topRight;
					newWidth = Math.Abs(fixedCorner.X - worldMouse.X) / scale.X;
					newHeight = Math.Abs(worldMouse.Y - fixedCorner.Y) / scale.Y;
					break;

				case RectangleHandleType.Top:
					fixedCorner = new Vector2(entityPos.X, bounds.Bottom);
					newWidth = areaLight.RectangleWidth;
					newHeight = Math.Abs(fixedCorner.Y - worldMouse.Y) / scale.Y;
					break;

				case RectangleHandleType.Bottom:
					fixedCorner = new Vector2(entityPos.X, bounds.Top);
					newWidth = areaLight.RectangleWidth;
					newHeight = Math.Abs(worldMouse.Y - fixedCorner.Y) / scale.Y;
					break;

				case RectangleHandleType.Left:
					fixedCorner = new Vector2(bounds.Right, entityPos.Y);
					newWidth = Math.Abs(fixedCorner.X - worldMouse.X) / scale.X;
					newHeight = areaLight.RectangleHeight;
					break;

				case RectangleHandleType.Right:
					fixedCorner = new Vector2(bounds.Left, entityPos.Y);
					newWidth = Math.Abs(worldMouse.X - fixedCorner.X) / scale.X;
					newHeight = areaLight.RectangleHeight;
					break;

				default:
					return;
			}

			const float minSize = 1f;
			newWidth = Math.Max(newWidth, minSize);
			newHeight = Math.Max(newHeight, minSize);

			areaLight.SetWidth(newWidth);
			areaLight.SetHeight(newHeight);

			var newBounds = areaLight.Bounds;
			var currentCenter = new Vector2(newBounds.X + newBounds.Width / 2f, newBounds.Y + newBounds.Height / 2f);
			var desiredCenter = fixedCorner + (worldMouse - fixedCorner) / 2f;
			var offset = desiredCenter - currentCenter;

			areaLight.Entity.Transform.Position += offset;
		}

		private RectangleF GetAreaLightWorldBounds(AreaLight areaLight)
		{
			if (areaLight?.Entity == null)
				return RectangleF.Empty;

			return areaLight.Bounds;
		}

		#endregion

		/// <summary>
		/// Resets the dragging state
		/// </summary>
		public void Reset()
		{
			_isDragging = false;
			_selectedBoxCollider = null;
			_selectedAreaLight = null;
			_selectedHandleType = RectangleHandleType.None;
			_originalBoxBounds.Clear();
			_originalAreaLightBounds.Clear();
		}
	}
}