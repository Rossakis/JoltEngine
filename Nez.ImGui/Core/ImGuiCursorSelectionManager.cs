using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez;
using Nez.DeferredLighting;
using Nez.ImGuiTools.Gizmos;
using Nez.ImGuiTools.UndoActions;
using Nez.Sprites;
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

		// Gizmo handlers
		private EntityTransformGizmoHandler _transformGizmoHandler;
		private EntityRotateGizmoHandler _rotateGizmoHandler;
		private EntityScaleGizmoHandler _scaleGizmoHandler;
		private PolygonColliderGizmoHandler _polygonGizmoHandler;
		private RectangleGizmoHandler _rectangleGizmoHandler;

		// Cycling selection state
		private List<Entity> _selectableCandidates = new();
		private int _currentCandidateIndex = -1;
		private float _lastDoubleClickTime = 0f;
		private Vector2 _lastDoubleClickPosition = Vector2.Zero;
		private const float DOUBLE_CLICK_TIMEOUT = 1f; // 1 second timeout
		private const float POSITION_TOLERANCE = 5f; // 5 pixels tolerance for considering it the "same" position

		public bool IsMouseOverGizmo { get; private set; }

		public ImGuiCursorSelectionManager(ImGuiManager imGuiManager)
		{
			_imGuiManager = imGuiManager;
			_transformGizmoHandler = new EntityTransformGizmoHandler();
			_rotateGizmoHandler = new EntityRotateGizmoHandler();
			_scaleGizmoHandler = new EntityScaleGizmoHandler();
			_polygonGizmoHandler = new PolygonColliderGizmoHandler();
			_rectangleGizmoHandler = new RectangleGizmoHandler();
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
				_transformGizmoHandler.Reset();
				_rotateGizmoHandler.Reset();
				_scaleGizmoHandler.Reset();
				_polygonGizmoHandler.Reset();
				_rectangleGizmoHandler.Reset();
				
				_isBoxSelecting = false;
				ResetCyclingSelection();

				Debug.Log("Reset all gizmo drag states.");
			}
			UpdateModifierKeys();

			// Safety check: if we're not pressing the mouse button, ensure drag states are reset
			if (!Input.LeftMouseButtonDown)
			{
				if (_polygonGizmoHandler.IsDragging)
				{
					Debug.Warn("PolygonCollider drag state was stuck. Resetting.");
					_polygonGizmoHandler.Reset();
				}

				if (_rectangleGizmoHandler.IsDragging)
				{
					Debug.Warn("BoxCollider/AreaLight drag state was stuck. Resetting.");
					_rectangleGizmoHandler.Reset();
				}

				if (_transformGizmoHandler.IsDragging)
				{
					Debug.Warn("Transform drag state was stuck. Resetting.");
					_transformGizmoHandler.Reset();
				}

				if (_rotateGizmoHandler.IsDragging)
				{
					Debug.Warn("Rotate drag state was stuck. Resetting.");
					_rotateGizmoHandler.Reset();
				}

				if (_scaleGizmoHandler.IsDragging)
				{
					Debug.Warn("Scale drag state was stuck. Resetting.");
					_scaleGizmoHandler.Reset();
				}
			}

			// Check for timeout on cycling selection
			if (_selectableCandidates.Count > 0)
			{
				float currentTime = (float)Time.TotalTime;
				if (currentTime - _lastDoubleClickTime > DOUBLE_CLICK_TIMEOUT)
				{
					ResetCyclingSelection();
				}
			}

			if (_imGuiManager.IsGameWindowFocused && IsCursorWithinGameWindow())
			{
				var camera = Core.Scene.Camera;
				var worldMouse = camera.ScreenToWorldPoint(Input.ScaledMousePosition);
				var selectedEntities = _imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities;

				if (Core.IsEditMode)
				{
					if (SelectionMode == CursorSelectionMode.Normal)
					{
						_transformGizmoHandler.Draw(selectedEntities, worldMouse, camera);
						IsMouseOverGizmo = _transformGizmoHandler.IsMouseOverGizmo;
					}
					else if (SelectionMode == CursorSelectionMode.Resize)
					{
						_scaleGizmoHandler.Draw(selectedEntities, worldMouse, camera);
						IsMouseOverGizmo = _scaleGizmoHandler.IsMouseOverGizmo;
					}
					else if (SelectionMode == CursorSelectionMode.Rotate)
					{
						_rotateGizmoHandler.Draw(selectedEntities, worldMouse, camera);
						IsMouseOverGizmo = _rotateGizmoHandler.IsMouseOverGizmo;
					}
					else if (SelectionMode == CursorSelectionMode.ColliderResize)
					{
						DrawColliderResizeGizmo();
					}
				}

				if (!IsMouseOverGizmo && Core.IsEditMode && 
					!_polygonGizmoHandler.IsDragging && !_rectangleGizmoHandler.IsDragging &&
					!_transformGizmoHandler.IsDragging && !_rotateGizmoHandler.IsDragging && !_scaleGizmoHandler.IsDragging)
					HandleBoxSelection();

				if (Input.DoubleLeftMouseButtonPressed)
				{
					if (!_ctrlDown && !_shiftDown)
						DeselectEntity();

					TrySelectEntityAtMouse();
					_isBoxSelecting = false;
				}
				else if (Input.LeftMouseButtonPressed && 
						!_transformGizmoHandler.IsDragging && !_rotateGizmoHandler.IsDragging && !_scaleGizmoHandler.IsDragging &&
						!IsMouseOverGizmo && !_ctrlDown && !_shiftDown && 
						!_polygonGizmoHandler.IsDragging && !_rectangleGizmoHandler.IsDragging)
				{
					DeselectEntity();
					ResetCyclingSelection();
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

		private void ResetCyclingSelection()
		{
			_selectableCandidates.Clear();
			_currentCandidateIndex = -1;
			_lastDoubleClickTime = 0f;
			_lastDoubleClickPosition = Vector2.Zero;
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
				var deferredLight = entity.GetComponent<DeferredLight>();

				if (sprite == null && collider == null && deferredLight == null)
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
			
			// Reset cycling selection after box selection
			ResetCyclingSelection();
		}

		private RectangleF GetEntityBounds(Entity entity, bool isSpriteSelectionOn = false)
		{
			var collider = entity.GetComponent<Collider>();
			if (collider != null)
				return collider.Bounds;

			// Check for DeferredLight components (PointLight, AreaLight, etc.)
			var deferredLight = entity.GetComponent<DeferredLight>();
			if (deferredLight != null)
				return deferredLight.Bounds;

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
			var mouseScreen = Input.ScaledMousePosition;
			float currentTime = (float)Time.TotalTime;
			
			// Check if this is a continuation of the cycling selection
			bool isCyclingContinuation = false;
			if (_selectableCandidates.Count > 0)
			{
				float timeSinceLastClick = currentTime - _lastDoubleClickTime;
				float positionDistance = Vector2.Distance(mouseScreen, _lastDoubleClickPosition);
				
				if (timeSinceLastClick <= DOUBLE_CLICK_TIMEOUT && positionDistance <= POSITION_TOLERANCE)
				{
					isCyclingContinuation = true;
				}
				else
				{
					// Reset if position changed or timeout occurred
					ResetCyclingSelection();
				}
			}

			// If we're continuing cycling, just advance to the next candidate
			if (isCyclingContinuation && _selectableCandidates.Count > 0)
			{
				_currentCandidateIndex = (_currentCandidateIndex + 1) % _selectableCandidates.Count;
				var selected = _selectableCandidates[_currentCandidateIndex];
				
				// Update last click time and position
				_lastDoubleClickTime = currentTime;
				_lastDoubleClickPosition = mouseScreen;
				
				// Treat Shift as Control for game window selection
				bool additive = _ctrlDown || _shiftDown;

				_imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(selected, additive, false);
				_imGuiManager.OpenMainEntityInspector(selected);

				if (!MathUtils.IsVectorNaNOrInfinite(selected.Transform.Position))
				{
					SetCameraTargetPosition(selected.Transform.Position);
				}
				
				return;
			}

			// Build the list of selectable entities at this position
			_selectableCandidates.Clear();
			_currentCandidateIndex = -1;

			// Priority 1: Entities with Colliders
			var collidersAtPosition = new List<(Entity entity, float distance)>();
			for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
			{
				var entity = Core.Scene.Entities[i];
				
				if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
					continue;

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
					collidersAtPosition.Add((entity, distance));
				}
			}

			// Sort colliders by distance and add to candidates
			collidersAtPosition.Sort((a, b) => a.distance.CompareTo(b.distance));
			foreach (var (entity, _) in collidersAtPosition)
			{
				_selectableCandidates.Add(entity);
			}

			// Priority 2: Entities with SpriteRenderer
			var spritesAtPosition = new List<(Entity entity, int renderLayer, float distance)>();
			for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
			{
				var entity = Core.Scene.Entities[i];
				
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
						float distance = Vector2.Distance(entity.Transform.Position, mouseWorld);
						spritesAtPosition.Add((entity, sprite.RenderLayer, distance));
					}
				}
			}

			// Sort sprites by render layer (lower = in front), then by distance
			spritesAtPosition.Sort((a, b) =>
			{
				int layerCompare = a.renderLayer.CompareTo(b.renderLayer);
				if (layerCompare != 0) return layerCompare;
				return a.distance.CompareTo(b.distance);
			});

			foreach (var (entity, _, _) in spritesAtPosition)
			{
				if (!_selectableCandidates.Contains(entity))
					_selectableCandidates.Add(entity);
			}

			// Priority 3: Entities with DeferredLight
			var lightsAtPosition = new List<(Entity entity, float distance)>();
			for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
			{
				var entity = Core.Scene.Entities[i];
				
				if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
					continue;
				
				if (!entity.IsSelectableInEditor)
					continue;
				
				var deferredLight = entity.GetComponent<DeferredLight>();
				if (deferredLight != null)
				{
					var bounds = deferredLight.Bounds;
					if (bounds.Contains(mouseWorld))
					{
						var lightCenter = new Vector2(
							bounds.X + bounds.Width * 0.5f,
							bounds.Y + bounds.Height * 0.5f
						);
						float distance = Vector2.Distance(mouseWorld, lightCenter);
						lightsAtPosition.Add((entity, distance));
					}
				}
			}

			// Sort lights by distance and add to candidates
			lightsAtPosition.Sort((a, b) => a.distance.CompareTo(b.distance));
			foreach (var (entity, _) in lightsAtPosition)
			{
				if (!_selectableCandidates.Contains(entity))
					_selectableCandidates.Add(entity);
			}

			// Priority 4: Fallback to entities without sprites/colliders/lights
			var fallbackEntities = new List<(Entity entity, float distance)>();
			for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
			{
				var entity = Core.Scene.Entities[i];
				
				if (MathUtils.IsVectorNaNOrInfinite(entity.Transform.Position))
					continue;
				
				if (!entity.IsSelectableInEditor)
					continue;
				
				if (entity.GetComponent<SpriteRenderer>() == null && 
				    entity.GetComponent<Collider>() == null &&
				    entity.GetComponent<DeferredLight>() == null)
				{
					float dist = Vector2.Distance(entity.Transform.Position, mouseWorld);
					if (dist < 16f)
					{
						fallbackEntities.Add((entity, dist));
					}
				}
			}

			// Sort fallback entities by distance and add to candidates
			fallbackEntities.Sort((a, b) => a.distance.CompareTo(b.distance));
			foreach (var (entity, _) in fallbackEntities)
			{
				if (!_selectableCandidates.Contains(entity))
					_selectableCandidates.Add(entity);
			}

			// Select the first candidate if any exist
			if (_selectableCandidates.Count > 0)
			{
				_currentCandidateIndex = 0;
				var selected = _selectableCandidates[_currentCandidateIndex];
				
				// Update last click time and position
				_lastDoubleClickTime = currentTime;
				_lastDoubleClickPosition = mouseScreen;

				// Treat Shift as Control for game window selection
				bool additive = _ctrlDown || _shiftDown;

				_imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(selected, additive, false);
				_imGuiManager.OpenMainEntityInspector(selected);

				if (!MathUtils.IsVectorNaNOrInfinite(selected.Transform.Position))
				{
					SetCameraTargetPosition(selected.Transform.Position);
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

			// Collect all colliders and lights from selected entities
			var polygonColliders = new List<PolygonCollider>();
			var boxColliders = new List<BoxCollider>();
			var areaLights = new List<AreaLight>();

			foreach (var entity in entityPane.SelectedEntities)
			{
				polygonColliders.AddRange(entity.GetComponents<PolygonCollider>());
				boxColliders.AddRange(entity.GetComponents<BoxCollider>());
				areaLights.AddRange(entity.GetComponents<AreaLight>());
			}

			// Draw polygon colliders (unless we're dragging a rectangle)
			if (polygonColliders.Count > 0 && !_rectangleGizmoHandler.IsDragging)
			{
				_polygonGizmoHandler.Draw(polygonColliders, worldMouse, camera);
				IsMouseOverGizmo = _polygonGizmoHandler.IsMouseOverGizmo;
			}

			// Draw box colliders and area lights (unless we're dragging a polygon)
			if ((boxColliders.Count > 0 || areaLights.Count > 0) && !_polygonGizmoHandler.IsDragging)
			{
				_rectangleGizmoHandler.Draw(boxColliders, areaLights, worldMouse, camera, _shiftDown);
				IsMouseOverGizmo = _rectangleGizmoHandler.IsMouseOverGizmo;
			}
		}
	}
}