using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Nez;
using Nez.Editor;
using Nez.ImGuiTools;
using Nez.ImGuiTools.UndoActions;
using Nez.PhysicsShapes;
using Nez.Sprites;
using Nez.Textures;
using Nez.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nez.ImGuiTools
{
	public enum CursorSelectionMode
	{
		Normal,
		Resize,
		Rotate,
		ColliderResize
	}

	/// <summary>
	/// Handles entity selection in the ImGui game window via cursor, including box selection and gizmo manipulation.
	/// </summary>
	public class ImGuiCursorSelectionManager
	{
		public CursorSelectionMode SelectionMode = CursorSelectionMode.Normal;

		private ImGuiManager _imGuiManager;
		private bool _ctrlDown;
		private bool _shiftDown;

		// Box selection state
		private bool _isBoxSelecting = false;
		private Vector2 _boxSelectStartWorld;
		private Vector2 _boxSelectEndWorld;
		private Vector2 mouseScreen;

		// Gizmo/dragging state
		private bool _draggingX = false;
		private bool _draggingY = false;

		// Gizmo rotate 
		private bool _draggingRotate = false;
		private float _dragStartAngle;
		private float _dragStartEntityRotation;

		// Gizmo scale
		private Dictionary<Entity, Vector2> _dragStartEntityScales = new();
		private Dictionary<Entity, Vector2> _dragEndEntityScales = new();
		private Vector2 _dragStartScaleMouse;
		private bool _draggingScaleX = false;
		private bool _draggingScaleY = false;

		private Vector2 _dragStartMousePos;
		private Vector2 _dragStartWorldMouse;
		private Dictionary<Entity, Vector2> _dragStartEntityPositions = new();
		private Dictionary<Entity, Vector2> _dragEndEntityPositions = new();

		// Collider resize state
		private bool _isDraggingColliderPoint = false;
		private PolygonCollider _selectedPolygonCollider;
		private int _selectedPointIndex = -1;
		private Vector2 _originalPointValue;
		private Dictionary<PolygonCollider, List<Vector2>> _originalPolygonPoints = new();

		// Box Collider resize state
		private bool _isDraggingBoxCollider = false;
		private BoxCollider _selectedBoxCollider;
		private BoxColliderHandleType _selectedHandleType;
		private RectangleF _originalBoxBounds;
		private Dictionary<BoxCollider, RectangleF> _originalDictBoxBounds = new();
		public bool IsMouseOverGizmo { get; private set; }

		public ImGuiCursorSelectionManager(ImGuiManager imGuiManager)
		{
			_imGuiManager = imGuiManager;
		}

		/// <summary>
		/// Call this from ImGuiManager.LayoutGui or Update to handle selection logic.
		/// </summary>
		public void UpdateSelection()
		{
			if (ImGui.IsKeyPressed(ImGuiKey._1) || ImGui.IsKeyPressed(ImGuiKey.Q))
				SelectionMode = CursorSelectionMode.Normal;
			else if (ImGui.IsKeyPressed(ImGuiKey._2) || ImGui.IsKeyPressed(ImGuiKey.E))
				SelectionMode = CursorSelectionMode.Resize;
			else if (ImGui.IsKeyPressed(ImGuiKey._3) || ImGui.IsKeyPressed(ImGuiKey.R))
				SelectionMode = CursorSelectionMode.Rotate;
			else if (ImGui.IsKeyPressed(ImGuiKey._4) || ImGui.IsKeyPressed(ImGuiKey.T))
				SelectionMode = CursorSelectionMode.ColliderResize;

			if (ImGui.IsKeyPressed(ImGuiKey.Escape))
			{
				_isDraggingColliderPoint = false;
				_isDraggingBoxCollider = false;
				_draggingX = false;
				_draggingY = false;
				_draggingRotate = false;
				_draggingScaleX = false;
				_draggingScaleY = false;
				_isBoxSelecting = false;

				_selectedPolygonCollider = null;
				_selectedPointIndex = -1;
				_originalPolygonPoints.Clear();

				_selectedBoxCollider = null;
				_selectedHandleType = BoxColliderHandleType.None;
				_originalDictBoxBounds.Clear();

				Debug.Log("Reset all gizmo drag states.");
			}
			UpdateModifierKeys();

			// Safety check: if we're not pressing the mouse button, ensure drag states are reset
			if (!Input.LeftMouseButtonDown)
			{
				if (_isDraggingColliderPoint)
				{
					Debug.Warn("PolygonCollider drag state was stuck. Resetting.");
					_isDraggingColliderPoint = false;
					_selectedPolygonCollider = null;
					_selectedPointIndex = -1;
					_originalPolygonPoints.Clear();
				}
				
				if (_isDraggingBoxCollider)
				{
					Debug.Warn("BoxCollider drag state was stuck. Resetting.");
					_isDraggingBoxCollider = false;
					_selectedBoxCollider = null;
					_selectedHandleType = BoxColliderHandleType.None;
					_originalDictBoxBounds.Clear();
				}
			}

			if (_imGuiManager.IsGameWindowFocused && IsCursorWithinGameWindow())
			{
				if (SelectionMode == CursorSelectionMode.Normal)
					DrawEntityArrowsGizmo();
				else if (SelectionMode == CursorSelectionMode.Resize)
					DrawEntityScaleGizmo();
				else if (SelectionMode == CursorSelectionMode.Rotate)
					DrawEntityRotateGizmo();
				else if (SelectionMode == CursorSelectionMode.ColliderResize)
					DrawColliderResizeGizmo();

				if (!IsMouseOverGizmo && Core.IsEditMode && !_isDraggingColliderPoint && !_isDraggingBoxCollider)
					HandleBoxSelection();

				if (Input.DoubleLeftMouseButtonPressed)
				{
					if (!_ctrlDown && !_shiftDown)
						DeselectEntity();

					TrySelectEntityAtMouse();
					_isBoxSelecting = false;
				}
				else if (Input.LeftMouseButtonPressed && !_draggingX && !_draggingY && !IsMouseOverGizmo &&
		         !_ctrlDown && !_shiftDown && !_isDraggingColliderPoint && !_isDraggingBoxCollider)
				{
					DeselectEntity();
				}
			}
		}

		public bool IsCursorWithinGameWindow()
		{
			var windowPos = _imGuiManager.GameWindowPosition;
			var windowSize = _imGuiManager.GameWindowSize;
			var mousePos = ImGui.GetIO().MousePos;

			bool withinX = mousePos.X >= windowPos.X && mousePos.X < windowPos.X + windowSize.X;
			bool withinY = mousePos.Y >= windowPos.Y && mousePos.Y < windowPos.Y + windowSize.Y;

			return withinX && withinY;
		}

		private void UpdateModifierKeys()
		{
			_ctrlDown = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeySuper;
			_shiftDown = ImGui.GetIO().KeyShift;
		}

		private void HandleBoxSelection()
		{
			mouseScreen = Core.Scene.Camera.ScreenToWorldPoint(Input.ScaledMousePosition);

			if (!_isBoxSelecting && Input.LeftMouseButtonPressed)
			{
				if (!_ctrlDown && !_shiftDown)
				{
					_imGuiManager.SceneGraphWindow.EntityPane.DeselectAllEntities();
					DeselectEntity();
				}

				_isBoxSelecting = true;
				_boxSelectStartWorld = Core.Scene.Camera.ScreenToWorldPoint(mouseScreen);
				_boxSelectEndWorld = _boxSelectStartWorld;
			}

			if (_isBoxSelecting && Input.LeftMouseButtonDown)
			{
				_boxSelectEndWorld = Core.Scene.Camera.ScreenToWorldPoint(mouseScreen);
				DrawSelectionBoxNez(_boxSelectStartWorld, _boxSelectEndWorld);
			}

			if (_isBoxSelecting && Input.LeftMouseButtonReleased)
			{
				_boxSelectEndWorld = Core.Scene.Camera.ScreenToWorldPoint(mouseScreen);
				SelectEntitiesInBox(_boxSelectStartWorld, _boxSelectEndWorld);
				_isBoxSelecting = false;
			}
		}

		private void DrawSelectionBoxNez(Vector2 worldStart, Vector2 worldEnd)
		{
			var camera = Core.Scene.Camera;
			var min = new Vector2(Math.Min(worldStart.X, worldEnd.X), Math.Min(worldStart.Y, worldEnd.Y));
			var max = new Vector2(Math.Max(worldStart.X, worldEnd.X), Math.Max(worldStart.Y, worldEnd.Y));
			var rect = new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y);
			Debug.DrawRect(camera.WorldToScreenRect(rect), Color.CornflowerBlue * 0.7f, 0f);
		}

		private void SelectEntitiesInBox(Vector2 worldStart, Vector2 worldEnd)
		{
			var camera = Core.Scene.Camera;
			var min = new Vector2(Math.Min(worldStart.X, worldEnd.X), Math.Min(worldStart.Y, worldEnd.Y));
			var max = new Vector2(Math.Max(worldStart.X, worldEnd.X), Math.Max(worldStart.Y, worldEnd.Y));
			var rect = new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y);
			var selectionRect = camera.WorldToScreenRect(rect);
			var selectedEntities = new List<Entity>();

			for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
			{
				var entity = Core.Scene.Entities[i];

				if(!entity.IsSelectableInEditor)
					continue;

				var sprite = entity.GetComponent<SpriteRenderer>();
				var collider = entity.GetComponent<Collider>();

				if (sprite == null && collider == null)
					continue;

				if (sprite != null)
					continue;

				RectangleF entityBounds = GetEntityBounds(entity);

				if (entityBounds.Width <= 0 || entityBounds.Height <= 0)
					continue;

				if (selectionRect.Intersects(entityBounds))
				{
					float intersectX = Math.Max(selectionRect.X, entityBounds.X);
					float intersectY = Math.Max(selectionRect.Y, entityBounds.Y);
					float intersectRight = Math.Min(selectionRect.X + selectionRect.Width,
						entityBounds.X + entityBounds.Width);
					float intersectBottom = Math.Min(selectionRect.Y + selectionRect.Height,
						entityBounds.Y + entityBounds.Height);

					float intersectWidth = intersectRight - intersectX;
					float intersectHeight = intersectBottom - intersectY;

					if (intersectWidth > 0 && intersectHeight > 0)
					{
						float intersectionArea = intersectWidth * intersectHeight;
						float entityArea = entityBounds.Width * entityBounds.Height;
						float coverage = intersectionArea / entityArea;

						if (coverage >= 0.4f)
						{
							selectedEntities.Add(entity);
						}
					}
				}
			}

			if (selectedEntities.Count > 0)
			{
				var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
				bool additive = _ctrlDown || _shiftDown;
				if (!additive)
					entityPane.DeselectAllEntities();

				// Add all entities in rectangle to selection
				foreach (var entity in selectedEntities)
					entityPane.SetSelectedEntity(entity, true, false); // always additive when rectangle + modifier

				_imGuiManager.OpenMainEntityInspector(selectedEntities[0]);
				SetCameraTargetPosition(selectedEntities[0].Transform.Position);
			}
		}

		private RectangleF GetEntityBounds(Entity entity, bool isSpriteSelectionOn = false)
		{
			var collider = entity.GetComponent<Collider>();
			if (collider != null)
				return collider.Bounds;

			// Sprite selection will not be supported with SelectEntitiesInBox
			if (isSpriteSelectionOn)
			{
				var sprite = entity.GetComponent<SpriteRenderer>();
				if (sprite != null)
					return sprite.Bounds;
			}

			var pos = entity.Transform.Position;
			return new RectangleF(pos.X - 8, pos.Y - 8, 16, 16);
		}

		public void DeselectEntity()
		{
			_imGuiManager.SceneGraphWindow.EntityPane.DeselectAllEntities();

			if (_imGuiManager.SceneGraphWindow?.EntityPane != null)
				_imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(null, false);
		}

		public void SetCameraTargetPosition(Vector2 position)
		{
			// Validate position before setting camera target
			if (MathUtils.IsVectorNaNOrInfinite(position))
			{
				Debug.Warn($"Attempted to set camera target to invalid position: {position}. Ignoring.");
				return;
			}
			
			_imGuiManager.CameraTargetPosition = _imGuiManager.SceneGraphWindow.EntityPane.GetSelectedEntitiesCenter();
		}

		private void TrySelectEntityAtMouse()
		{
			var mouseWorld = Core.Scene.Camera.ScreenToWorldPoint(Input.ScaledMousePosition);
			Entity selected = null;

			// Priority 1= Entities with Colliders (highest)
			Entity closestColliderEntity = null;
			float closestColliderDistance = float.MaxValue;

			for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
			{
				var entity = Core.Scene.Entities[i];
				
				if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
				{
					Debug.Warn($"Entity '{entity.Name}' has invalid position: {entity.Transform.Position}. Skipping selection.");
					continue;
				}

				if (!entity.IsSelectableInEditor)
					continue;

				var collider = entity.GetComponent<Collider>();
				if (collider != null && collider.Bounds.Contains(mouseWorld))
				{
					var colliderCenter = new Vector2(
						collider.Bounds.X + collider.Bounds.Width * 0.5f,
						collider.Bounds.Y + collider.Bounds.Height * 0.5f
					);
					float distance = Vector2.Distance(mouseWorld, colliderCenter);
					
					if (distance < closestColliderDistance)
					{
						closestColliderDistance = distance;
						closestColliderEntity = entity;
					}
				}
			}

			if (closestColliderEntity != null)
			{
				selected = closestColliderEntity;
			}
			else
			{
				// Priority 2 = Entities with SpriteRenderer
				Entity lowestRenderLayerSprite = null;
				int lowestRenderLayer = int.MaxValue;
				float minDist = 16f;

				for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
				{
					var entity = Core.Scene.Entities[i];
					
					// Skip entities with invalid positions
					if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
						continue;
					
					if (!entity.IsSelectableInEditor)
						continue;
					
					var sprite = entity.GetComponent<SpriteRenderer>();
					if (sprite != null)
					{
						var bounds = sprite.Bounds;
						if (bounds.Contains(mouseWorld))
						{
							// Check if this sprite has a lower render layer (renders on top)
							// Lower value = renders later = appears in front
							if (sprite.RenderLayer < lowestRenderLayer)
							{
								lowestRenderLayer = sprite.RenderLayer;
								lowestRenderLayerSprite = entity;
							}
							else if (sprite.RenderLayer == lowestRenderLayer)
							{
								// If render layers are equal, prefer the closer one
								if (lowestRenderLayerSprite != null)
								{
									float currentDist = Vector2.Distance(entity.Transform.Position, mouseWorld);
									float existingDist = Vector2.Distance(lowestRenderLayerSprite.Transform.Position, mouseWorld);
									
									if (currentDist < existingDist)
									{
										lowestRenderLayerSprite = entity;
									}
								}
								else
								{
									lowestRenderLayerSprite = entity;
								}
							}
						}
					}
				}

				selected = lowestRenderLayerSprite;

				// Priority 3 = Fallback to entities without sprites/colliders
				if (selected == null)
				{
					float minDistFallback = 16f;
					for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
					{
						var entity = Core.Scene.Entities[i];
						
						if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
							continue;
						
						if (!entity.IsSelectableInEditor)
							continue;
						
						if (entity.GetComponent<SpriteRenderer>() == null && 
						    entity.GetComponent<Collider>() == null)
						{
							float dist = Vector2.Distance(entity.Transform.Position, mouseWorld);
							if (dist < minDistFallback)
							{
								selected = entity;
								minDistFallback = dist;
							}
						}
					}
				}
			}

			if (selected != null)
			{
				// Treat Shift as Control for game window selection
				bool additive = _ctrlDown || _shiftDown;

				_imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(selected, additive, false);
				_imGuiManager.OpenMainEntityInspector(selected);

				if (MathUtils.IsVectorNaNOrInfinite(selected.Transform.Position))
				{
					Debug.Error($"Selected entity '{selected.Name}' has invalid position. Not setting camera target.");
					return;
				}
				
				SetCameraTargetPosition(selected.Transform.Position);
			}
		}

		/// <summary>
		/// Draws the X/Y axis arrows for the selected entity and handles axis hover.
		/// </summary>
		private void DrawEntityArrowsGizmo()
		{
			var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
			IsMouseOverGizmo = false;

			if (entityPane.SelectedEntities.Count == 0 || !Core.IsEditMode)
				return;

			// Calculate center, skipping entities with invalid positions
			Vector2 center = Vector2.Zero;
			int validEntityCount = 0;
			
			foreach (var e in entityPane.SelectedEntities)
			{
				// Skip entities with NaN or Infinity positions
				if (MathUtils.IsVectorNaNOrInfinite(e.Transform.Position))
				{
					Debug.Warn($"Entity '{e.Name}' has invalid position: {e.Transform.Position}. Skipping from gizmo center calculation.");
					continue;
				}
				
				center += e.Transform.Position;
				validEntityCount++;
			}
			
			// If no valid entities, don't draw gizmo
			if (validEntityCount == 0)
			{
				Debug.Warn("No valid entities to draw gizmo for.");
				return;
			}
			
			center /= validEntityCount;

			var camera = Core.Scene.Camera;
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

			// Axis hover and drag logic
			HandleEntityDragging();
		}

		/// <summary>
		/// Handles entity dragging based on gizmo axis hover.
		/// </summary>
		private void HandleEntityDragging()
		{
			var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
			var selectedEntities = entityPane.SelectedEntities;
			if (selectedEntities.Count == 0 || !Core.IsEditMode)
				return;

			var camera = Core.Scene.Camera;
			var mousePos = Input.ScaledMousePosition;
			var worldMouse = camera.ScreenToWorldPoint(mousePos);

			// Compute gizmo axis positions (skip invalid entities)
			Vector2 center = Vector2.Zero;
			int validEntityCount = 0;
			
			foreach (var e in selectedEntities)
			{
				if (MathUtils.IsVectorNaNOrInfinite(e.Transform.Position))
				{
					continue;
				}
				
				center += e.Transform.Position;
				validEntityCount++;
			}
			
			if (validEntityCount == 0)
				return;
		
			center /= validEntityCount;

			float baseLength = 30f;
			float minLength = 10f;
			float maxLength = 100f;
			float axisLength = baseLength / MathF.Max(camera.RawZoom, 0.01f);
			axisLength = Math.Clamp(axisLength, minLength, maxLength);

			var screenPos = camera.WorldToScreenPoint(center);
			var axisEndX = camera.WorldToScreenPoint(center + new Vector2(axisLength, 0));
			var axisEndY = camera.WorldToScreenPoint(center + new Vector2(0, -axisLength));

			bool xHovered = IsMouseNearLine(mousePos, screenPos, axisEndX);
			bool yHovered = IsMouseNearLine(mousePos, screenPos, axisEndY);

			// Start dragging if not already dragging
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
						// Skip entities with invalid positions
						if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
						{
							continue;
						}
						
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
					// Skip entities with invalid positions
					if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
					{
						continue;
					}
					
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
			if ((_draggingX || _draggingY) && !Input.LeftMouseButtonDown)
			{
				_draggingX = false;
				_draggingY = false;

				_dragEndEntityPositions = new Dictionary<Entity, Vector2>();
				foreach (var entity in selectedEntities)
				{
					// Skip entities with invalid positions
					if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
					{
						continue;
					}
					
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

		private void DrawEntityRotateGizmo()
		{
			var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
			IsMouseOverGizmo = false;

			if (entityPane.SelectedEntities.Count == 0 || !Core.IsEditMode)
				return;

			Vector2 center = Vector2.Zero;
			foreach (var e in entityPane.SelectedEntities)
				center += e.Transform.Position;
			center /= entityPane.SelectedEntities.Count;

			var camera = Core.Scene.Camera;
			var screenCenter = camera.WorldToScreenPoint(center);

			float baseRadius = 30f;
			float minRadius = 28f;
			float maxRadius = 33f;
			float radius = baseRadius / MathF.Max(camera.RawZoom, 0.01f);
			radius = Math.Clamp(radius, minRadius, maxRadius);

			Color circleColor = Color.CornflowerBlue;

			var mousePos = Input.ScaledMousePosition;
			float distToCenter = Vector2.Distance(mousePos, screenCenter);

			// Only allow rotation if cursor is inside the circle
			bool hoveredCircle = distToCenter <= radius;

			if (hoveredCircle)
			{
				circleColor = Color.Orange;
				IsMouseOverGizmo = true;
			}
			if (_draggingRotate)
				circleColor = Color.Yellow;

			Debug.DrawCircle(center, radius / camera.RawZoom, circleColor);

			// Draw up (Y) and right (X) axes for visual reference only
			DrawRotateGizmoAxesUpRight(center, radius, camera, entityPane.SelectedEntities[0].Transform.Rotation);

			// Start rotation only if mouse is inside the circle
			if (!_draggingRotate && hoveredCircle && Input.LeftMouseButtonPressed)
			{
				_draggingRotate = true;
				_dragStartMousePos = mousePos;
				_dragStartAngle = MathF.Atan2(mousePos.Y - screenCenter.Y, mousePos.X - screenCenter.X);
				_dragStartEntityRotation = entityPane.SelectedEntities[0].Transform.Rotation;
			}

			// Apply rotation as long as we're dragging inside the circle
			if (_draggingRotate && Input.LeftMouseButtonDown)
			{
				var currentAngle = MathF.Atan2(mousePos.Y - screenCenter.Y, mousePos.X - screenCenter.X);
				float deltaAngle = currentAngle - _dragStartAngle;

				if (deltaAngle > MathF.PI) deltaAngle -= MathF.PI * 2;
				if (deltaAngle < -MathF.PI) deltaAngle += MathF.PI * 2;

				foreach (var entity in entityPane.SelectedEntities)
					entity.Transform.Rotation = _dragStartEntityRotation + deltaAngle;

				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
			}

			if (_draggingRotate && Input.LeftMouseButtonReleased)
			{
				_draggingRotate = false;

				var startRotations = new Dictionary<Entity, float>();
				var endRotations = new Dictionary<Entity, float>();
				foreach (var entity in entityPane.SelectedEntities)
				{
					startRotations[entity] = _dragStartEntityRotation;
					endRotations[entity] = entity.Transform.Rotation;
				}

				bool anyRotated = entityPane.SelectedEntities.Any(e =>
					startRotations.TryGetValue(e, out var startRot) &&
					endRotations.TryGetValue(e, out var endRot) &&
					startRot != endRot
				);

				if (anyRotated)
				{
					EditorChangeTracker.PushUndo(
						new MultiEntityRotationUndoAction(
							entityPane.SelectedEntities.ToList(),
							startRotations,
							endRotations,
							$"Rotated {string.Join(", ", entityPane.SelectedEntities.Select(e => e.Name))}"
						),
						entityPane.SelectedEntities.First(),
						$"Rotated {string.Join(", ", entityPane.SelectedEntities.Select(e => e.Name))}"
					);
				}
			}
		}

		// Draws only up (Y) and right (X) axes inside the rotate gizmo for visual reference
		private void DrawRotateGizmoAxesUpRight(Vector2 center, float radius, Camera camera, float rotation, Vector2? mousePos = null)
		{
			float axisLength = radius * 0.7f / camera.RawZoom;

			// Right (X) axis
			Vector2 axisXDir = new Vector2(MathF.Cos(rotation), MathF.Sin(rotation));
			Vector2 axisXStart = center;
			Vector2 axisXEnd = center + axisXDir * axisLength;

			// Up (Y) axis (90 degrees offset)
			float axisYAngle = rotation - MathF.PI / 2f;
			Vector2 axisYDir = new Vector2(MathF.Cos(axisYAngle), MathF.Sin(axisYAngle));
			Vector2 axisYStart = center;
			Vector2 axisYEnd = center + axisYDir * axisLength;

			Debug.DrawLine(axisXStart, axisXEnd, Color.Red);
			Debug.DrawLine(axisYStart, axisYEnd, Color.LimeGreen);
		}

		private void DrawEntityScaleGizmo()
		{
			var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
			IsMouseOverGizmo = false;

			if (entityPane.SelectedEntities.Count == 0 || !Core.IsEditMode)
				return;

			// Compute center of all selected entities
			Vector2 center = Vector2.Zero;
			foreach (var e in entityPane.SelectedEntities)
				center += e.Transform.Position;
			center /= entityPane.SelectedEntities.Count;

			var camera = Core.Scene.Camera;
			float baseLength = 30f;
			float minLength = 10f;
			float maxLength = 100f;
			float axisLength = baseLength / MathF.Max(camera.RawZoom, 0.01f);
			axisLength = Math.Clamp(axisLength, minLength, maxLength);

			var screenPos = camera.WorldToScreenPoint(center);

			// If dragging, move only the selected axis's end to follow the mouse position instantly
			Vector2 axisEndX, axisEndY;
			var mousePos = Input.ScaledMousePosition;
			var worldMouse = camera.ScreenToWorldPoint(mousePos);

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

			HandleEntityScaleDragging(center, axisLength, xHovered, yHovered);
		}

		private void HandleEntityScaleDragging(Vector2 center, float axisLength, bool xHovered, bool yHovered)
		{
			var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
			var selectedEntities = entityPane.SelectedEntities;
			if (selectedEntities.Count == 0 || !Core.IsEditMode)
				return;

			var camera = Core.Scene.Camera;
			var mousePos = Input.ScaledMousePosition;
			var worldMouse = camera.ScreenToWorldPoint(mousePos);

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

					// No need to set _scaleGizmoAxisLengthOverride here, handled in DrawEntityScaleGizmo
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
			if ((_draggingScaleX || _draggingScaleY) && !Input.LeftMouseButtonDown)
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

		private void DrawColliderResizeGizmo()
		{
			var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
			IsMouseOverGizmo = false;

			if (entityPane.SelectedEntities.Count == 0 || !Core.IsEditMode)
				return;

			var camera = Core.Scene.Camera;
			var mousePos = Input.ScaledMousePosition;
			var worldMouse = camera.ScreenToWorldPoint(mousePos);

			// Collect all PolygonColliders and BoxColliders from selected entities
			var polygonColliders = new List<PolygonCollider>();
			var boxColliders = new List<BoxCollider>();
			
			foreach (var entity in entityPane.SelectedEntities)
			{
				var polyColliders = entity.GetComponents<PolygonCollider>();
				polygonColliders.AddRange(polyColliders);
				
				var boxColls = entity.GetComponents<BoxCollider>();
				boxColliders.AddRange(boxColls);
			}

			// Draw polygon colliders (unless we're dragging a box collider)
			if (polygonColliders.Count > 0 && !_isDraggingBoxCollider)
			{
				DrawPolygonColliderGizmos(polygonColliders, worldMouse, camera);
			}

			// Draw box colliders (unless we're dragging a polygon collider)
			if (boxColliders.Count > 0 && !_isDraggingColliderPoint)
			{
				DrawBoxColliderGizmos(boxColliders, worldMouse, camera);
			}
		}

		#region Polygon Collider Gizmos (existing code refactored)

		private void DrawPolygonColliderGizmos(List<PolygonCollider> polygonColliders, Vector2 worldMouse, Camera camera)
		{
			var mousePos = Input.ScaledMousePosition;
			
			// Start dragging
			if (!_isDraggingColliderPoint && Input.LeftMouseButtonPressed)
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
					_isDraggingColliderPoint = true;
					_selectedPolygonCollider = closestCollider;
					_selectedPointIndex = closestPointIndex;
					_originalPointValue = _selectedPolygonCollider.Points[_selectedPointIndex];

					// Store original points for undo
					_originalPolygonPoints.Clear();
					foreach (var collider in polygonColliders)
					{
						_originalPolygonPoints[collider] = new List<Vector2>(collider.Points);
					}
				}
			}

			// During dragging
			if (_isDraggingColliderPoint && Input.LeftMouseButtonDown)
			{
				if (_selectedPolygonCollider != null && _selectedPointIndex >= 0)
				{
					var worldCenter = _selectedPolygonCollider.Entity.Transform.Position + _selectedPolygonCollider.LocalOffset;
					var newLocalPoint = worldMouse - worldCenter;

					_selectedPolygonCollider.Points[_selectedPointIndex] = newLocalPoint;
					_selectedPolygonCollider.UpdateShapeFromPoints();

					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
					IsMouseOverGizmo = true;
				}
			}

			// End dragging
			if (_isDraggingColliderPoint && Input.LeftMouseButtonReleased)
			{
				if (_selectedPolygonCollider != null)
				{
					var finalPoints = new Dictionary<PolygonCollider, List<Vector2>>();
					finalPoints[_selectedPolygonCollider] = new List<Vector2>(_selectedPolygonCollider.Points);

					EditorChangeTracker.PushUndo(
						new PolygonColliderPointEditUndoAction(
							_selectedPolygonCollider,
							_originalPolygonPoints[_selectedPolygonCollider],
							finalPoints[_selectedPolygonCollider],
							$"Edited PolygonCollider point on {_selectedPolygonCollider.Entity.Name}"
						),
						_selectedPolygonCollider.Entity,
						$"Edited PolygonCollider point"
					);
				}

				_isDraggingColliderPoint = false;
				_selectedPolygonCollider = null;
				_selectedPointIndex = -1;
				_originalPolygonPoints.Clear();
			}

			// Draw all polygon points
			float pointSize = 6f / camera.RawZoom;
			foreach (var collider in polygonColliders)
			{
				var polygon = collider.Shape as Polygon;
				if (polygon == null || polygon.Points == null)
					continue;

				for (int i = 0; i < polygon.Points.Length; i++)
				{
					var worldPoint = collider.Entity.Transform.Position + collider.LocalOffset + polygon.Points[i];
					
					bool isSelected = _isDraggingColliderPoint && 
					                 collider == _selectedPolygonCollider && 
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
				
				// Draw center point for reference
				var centerPoint = collider.Entity.Transform.Position + collider.LocalOffset;
				Debug.DrawCircle(centerPoint, 4f / camera.RawZoom, Color.Red);
			}
		}

		#endregion

		#region Box Collider Gizmos (new code)

		private enum BoxColliderHandleType
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

		private void DrawBoxColliderGizmos(List<BoxCollider> boxColliders, Vector2 worldMouse, Camera camera)
		{
			float handleSize = 6f / camera.RawZoom;
			float edgeThreshold = 8f / camera.RawZoom;

			// Start dragging
			if (!_isDraggingBoxCollider && Input.LeftMouseButtonPressed)
			{
				BoxCollider closestCollider = null;
				BoxColliderHandleType closestHandle = BoxColliderHandleType.None;
				float closestDistance = float.MaxValue;

				foreach (var collider in boxColliders)
				{
					var bounds = GetBoxColliderWorldBounds(collider);

					// Calculate corner positions
					var topLeft = new Vector2(bounds.Left, bounds.Top);
					var topRight = new Vector2(bounds.Right, bounds.Top);
					var bottomLeft = new Vector2(bounds.Left, bounds.Bottom);
					var bottomRight = new Vector2(bounds.Right, bounds.Bottom);

					var corners = new[]
					{
						(topLeft, BoxColliderHandleType.TopLeft),
						(topRight, BoxColliderHandleType.TopRight),
						(bottomLeft, BoxColliderHandleType.BottomLeft),
						(bottomRight, BoxColliderHandleType.BottomRight)
					};

					foreach (var (corner, handleType) in corners)
					{
						float dist = Vector2.Distance(worldMouse, corner);
						if (dist < handleSize && dist < closestDistance)
						{
							closestDistance = dist;
							closestCollider = collider;
							closestHandle = handleType;
						}
					}

					// If no corner selected, check edges
					if (closestHandle == BoxColliderHandleType.None)
					{
						// Top edge
						if (Math.Abs(worldMouse.Y - bounds.Top) < edgeThreshold &&
						    worldMouse.X >= bounds.Left && worldMouse.X <= bounds.Right)
						{
							float dist = Math.Abs(worldMouse.Y - bounds.Top);
							if (dist < closestDistance)
							{
								closestDistance = dist;
								closestCollider = collider;
								closestHandle = BoxColliderHandleType.Top;
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
								closestCollider = collider;
								closestHandle = BoxColliderHandleType.Bottom;
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
								closestCollider = collider;
								closestHandle = BoxColliderHandleType.Left;
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
								closestCollider = collider;
								closestHandle = BoxColliderHandleType.Right;
							}
						}
					}
				}

				if (closestCollider != null && closestHandle != BoxColliderHandleType.None)
				{
					_isDraggingBoxCollider = true;
					_selectedBoxCollider = closestCollider;
					_selectedHandleType = closestHandle;

					// Store original bounds for undo
					_originalDictBoxBounds.Clear();
					foreach (var collider in boxColliders)
					{
						_originalDictBoxBounds[collider] = GetBoxColliderWorldBounds(collider);
					}
				}
			}

			// During dragging
			if (_isDraggingBoxCollider && Input.LeftMouseButtonDown)
			{
				if (_selectedBoxCollider != null)
				{
					ResizeBoxCollider(_selectedBoxCollider, worldMouse, _selectedHandleType);
					IsMouseOverGizmo = true;

					// Set appropriate cursor based on handle type
					switch (_selectedHandleType)
					{
						case BoxColliderHandleType.TopLeft:
						case BoxColliderHandleType.BottomRight:
							ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
							break;
						case BoxColliderHandleType.TopRight:
						case BoxColliderHandleType.BottomLeft:
							ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNESW);
							break;
						case BoxColliderHandleType.Top:
						case BoxColliderHandleType.Bottom:
							ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
							break;
						case BoxColliderHandleType.Left:
						case BoxColliderHandleType.Right:
							ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
							break;
					}
				}
			}

			// End dragging
			if (_isDraggingBoxCollider && Input.LeftMouseButtonReleased)
			{
				if (_selectedBoxCollider != null)
				{
					var finalBounds = new Dictionary<BoxCollider, RectangleF>();
					finalBounds[_selectedBoxCollider] = GetBoxColliderWorldBounds(_selectedBoxCollider);

					EditorChangeTracker.PushUndo(
						new BoxColliderResizeUndoAction(
							_selectedBoxCollider,
							_originalDictBoxBounds[_selectedBoxCollider],
							finalBounds[_selectedBoxCollider],
							$"Resized BoxCollider on {_selectedBoxCollider.Entity.Name}"
						),
						_selectedBoxCollider.Entity,
						$"Resized BoxCollider"
					);
				}

				_isDraggingBoxCollider = false;
				_selectedBoxCollider = null;
				_selectedHandleType = BoxColliderHandleType.None;
				_originalDictBoxBounds.Clear();
			}

			// Draw handles - use calculated bounds, not cached Bounds property
			foreach (var collider in boxColliders)
			{
				var bounds = GetBoxColliderWorldBounds(collider);

				var corners = new[]
				{
					(new Vector2(bounds.Left, bounds.Top), BoxColliderHandleType.TopLeft),
					(new Vector2(bounds.Right, bounds.Top), BoxColliderHandleType.TopRight),
					(new Vector2(bounds.Left, bounds.Bottom), BoxColliderHandleType.BottomLeft),
					(new Vector2(bounds.Right, bounds.Bottom), BoxColliderHandleType.BottomRight)
				};

				foreach (var (corner, handleType) in corners)
				{
					bool isSelected = _isDraggingBoxCollider && 
					                 collider == _selectedBoxCollider && 
					                 _selectedHandleType == handleType;

					var pointColor = isSelected ? Color.Yellow : Color.Magenta;
					
					Debug.DrawHollowRect(new RectangleF(
						corner.X - handleSize / 2f,
						corner.Y - handleSize / 2f,
						handleSize,
						handleSize
					), pointColor);
				}

				Debug.DrawHollowRect(bounds, Color.Cyan * 0.5f);
				
				var center = new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
				Debug.DrawCircle(center, 4f / camera.RawZoom, Color.Red);
			}
		}

		private void ResizeBoxCollider(BoxCollider collider, Vector2 worldMouse, BoxColliderHandleType handleType)
		{
			var box = collider.Shape as Box;
			if (box == null) 
				return;

			bool mirroredScaling = _shiftDown;

			if (mirroredScaling)
			{
				// MIRRORED MODE: Keep center fixed, use existing logic
				ResizeBoxColliderMirrored(collider, worldMouse, handleType);
			}
			else
			{
				// NORMAL MODE: Per-vertex editing, opposite corner stays fixed
				ResizeBoxColliderVertex(collider, worldMouse, handleType);
			}
		}

		private void ResizeBoxColliderMirrored(BoxCollider collider, Vector2 worldMouse, BoxColliderHandleType handleType)
		{
			var bounds = collider.Bounds;
			var box = collider.Shape as Box;
			
			// Store original center
			var originalCenter = new Vector2(
				bounds.X + bounds.Width * 0.5f,
				bounds.Y + bounds.Height * 0.5f
			);

			// Calculate new bounds based on handle type (existing mirrored logic)
			var newLeft = bounds.Left;
			var newTop = bounds.Top;
			var newRight = bounds.Right;
			var newBottom = bounds.Bottom;

			switch (handleType)
			{
				case BoxColliderHandleType.TopLeft:
					var topLeftDelta = worldMouse - new Vector2(bounds.Left, bounds.Top);
					newLeft = worldMouse.X;
					newTop = worldMouse.Y;
					newRight = bounds.Right - topLeftDelta.X;
					newBottom = bounds.Bottom - topLeftDelta.Y;
					break;
					
				case BoxColliderHandleType.TopRight:
					var topRightDelta = worldMouse - new Vector2(bounds.Right, bounds.Top);
					newRight = worldMouse.X;
					newTop = worldMouse.Y;
					newLeft = bounds.Left - topRightDelta.X;
					newBottom = bounds.Bottom - topRightDelta.Y;
					break;
					
				case BoxColliderHandleType.BottomLeft:
					var bottomLeftDelta = worldMouse - new Vector2(bounds.Left, bounds.Bottom);
					newLeft = worldMouse.X;
					newBottom = worldMouse.Y;
					newRight = bounds.Right - bottomLeftDelta.X;
					newTop = bounds.Top - bottomLeftDelta.Y;
					break;
					
				case BoxColliderHandleType.BottomRight:
					var bottomRightDelta = worldMouse - new Vector2(bounds.Right, bounds.Bottom);
					newRight = worldMouse.X;
					newBottom = worldMouse.Y;
					newLeft = bounds.Left - bottomRightDelta.X;
					newTop = bounds.Top - bottomRightDelta.Y;
					break;
					
				case BoxColliderHandleType.Top:
					var topDelta = worldMouse.Y - bounds.Top;
					newTop = worldMouse.Y;
					newBottom = bounds.Bottom - topDelta;
					break;
					
				case BoxColliderHandleType.Bottom:
					var bottomDelta = worldMouse.Y - bounds.Bottom;
					newBottom = worldMouse.Y;
					newTop = bounds.Top - bottomDelta;
					break;
					
				case BoxColliderHandleType.Left:
					var leftDelta = worldMouse.X - bounds.Left;
					newLeft = worldMouse.X;
					newRight = bounds.Right - leftDelta;
					break;
					
				case BoxColliderHandleType.Right:
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

		private void ResizeBoxColliderVertex(BoxCollider collider, Vector2 worldMouse, BoxColliderHandleType handleType)
		{
			var box = collider.Shape as Box;
			var entityPos = collider.Entity.Transform.Position;

			// Convert world mouse position to collider-local space
			// collider-local = world - entity position - local offset
			var localToCollider = worldMouse - entityPos - collider.LocalOffset;

			var points = box.Points;

			// Determine which vertices to move based on handle type
			switch (handleType)
			{
				case BoxColliderHandleType.TopLeft:
					// Move top-left corner (point 0)
					// Keep bottom-right corner (point 2) fixed
					points[0] = new Vector2(localToCollider.X, localToCollider.Y);
					// Adjust adjacent corners to maintain rectangle
					points[1] = new Vector2(points[1].X, localToCollider.Y); // top-right Y
					points[3] = new Vector2(localToCollider.X, points[3].Y); // bottom-left X
					break;

				case BoxColliderHandleType.TopRight:
					// Move top-right corner (point 1)
					// Keep bottom-left corner (point 3) fixed
					points[1] = new Vector2(localToCollider.X, localToCollider.Y);
					points[0] = new Vector2(points[0].X, localToCollider.Y); // top-left Y
					points[2] = new Vector2(localToCollider.X, points[2].Y); // bottom-right X
					break;

				case BoxColliderHandleType.BottomRight:
					// Move bottom-right corner (point 2)
					// Keep top-left corner (point 0) fixed
					points[2] = new Vector2(localToCollider.X, localToCollider.Y);
					points[1] = new Vector2(localToCollider.X, points[1].Y); // top-right X
					points[3] = new Vector2(points[3].X, localToCollider.Y); // bottom-left Y
					break;

				case BoxColliderHandleType.BottomLeft:
					// Move bottom-left corner (point 3)
					// Keep top-right corner (point 1) fixed
					points[3] = new Vector2(localToCollider.X, localToCollider.Y);
					points[0] = new Vector2(localToCollider.X, points[0].Y); // top-left X
					points[2] = new Vector2(points[2].X, localToCollider.Y); // bottom-right Y
					break;

				case BoxColliderHandleType.Top:
					// Move top edge (points 0 and 1)
					points[0] = new Vector2(points[0].X, localToCollider.Y);
					points[1] = new Vector2(points[1].X, localToCollider.Y);
					break;

				case BoxColliderHandleType.Bottom:
					// Move bottom edge (points 2 and 3)
					points[2] = new Vector2(points[2].X, localToCollider.Y);
					points[3] = new Vector2(points[3].X, localToCollider.Y);
					break;

				case BoxColliderHandleType.Left:
					// Move left edge (points 0 and 3)
					points[0] = new Vector2(localToCollider.X, points[0].Y);
					points[3] = new Vector2(localToCollider.X, points[3].Y);
					break;

				case BoxColliderHandleType.Right:
					// Move right edge (points 1 and 2)
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
			{
				return;
			}

			if (collider.Entity != null && collider.Enabled)
				Physics.UpdateCollider(collider);
		}

		/// <summary>
		/// Calculates the current world-space bounds from the BoxCollider's points.
		/// This bypasses the cached Bounds property to get real-time updates.
		/// </summary>
		private RectangleF GetBoxColliderWorldBounds(BoxCollider collider)
		{
			var box = collider.Shape as Box;
			if (box == null || box.Points == null || box.Points.Length == 0)
				return collider.Bounds; // Fallback to cached bounds if something is wrong

			var entityPos = collider.Entity.Transform.Position;
			var localOffset = collider.LocalOffset;

			// Calculate world-space bounds from the current points
			var minX = float.MaxValue;
			var minY = float.MaxValue;
			var maxX = float.MinValue;
			var maxY = float.MinValue;

			foreach (var point in box.Points)
			{
				// Convert local point to world space
				var worldPoint = entityPos + localOffset + point;

				if (worldPoint.X < minX) minX = worldPoint.X;
				if (worldPoint.Y < minY) minY = worldPoint.Y;
				if (worldPoint.X > maxX) maxX = worldPoint.X;
				if (worldPoint.Y > maxY) maxY = worldPoint.Y;
			}

			return new RectangleF(minX, minY, maxX - minX, maxY - minY);
		}

		#endregion
		private bool IsMouseNearLine(Vector2 mouse, Vector2 a, Vector2 b, float threshold = 10f)
		{
			var ap = mouse - a;
			var ab = b - a;
			float abLen = ab.Length();
			float t = Math.Clamp(Vector2.Dot(ap, ab) / (abLen * abLen), 0, 1);
			var closest = a + ab * t;
			return (mouse - closest).Length() < threshold;
		}
	}
}