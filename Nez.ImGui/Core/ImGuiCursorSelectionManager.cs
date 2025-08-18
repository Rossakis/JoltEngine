using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Nez;
using Nez.Editor;
using Nez.ImGuiTools;
using System;
using Nez.Sprites;
using Nez.Utils;
using System.Collections.Generic;
using System.Linq;
using Nez.ImGuiTools.UndoActions;

namespace Nez.ImGuiTools
{
    /// <summary>
    /// Handles entity selection in the ImGui game window via cursor, including box selection and gizmo manipulation.
    /// </summary>
    public class ImGuiCursorSelectionManager
    {
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
        private Vector2 _dragStartWorldMouse;
        private Dictionary<Entity, Vector2> _dragStartEntityPositions = new();
        private Dictionary<Entity, Vector2> _dragEndEntityPositions = new();

        // Gizmo hover state
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
            UpdateModifierKeys();
            DrawAndHandleEntityGizmo();

			if(!IsMouseOverGizmo)
				HandleBoxSelection(); // Don't make the box selection if the mouse is over the gizmo

			HandleEntityDragging();

            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                TrySelectEntityAtMouse();
                _isBoxSelecting = false;
            }
            else if (Input.LeftMouseButtonPressed && !_draggingX && !_draggingY && !IsMouseOverGizmo)
            {
                _imGuiManager.SceneGraphWindow.EntityPane.DeselectAllEntities();
                DeselectEntity();
            }
        }

        private void UpdateModifierKeys()
        {
            _ctrlDown = Input.IsKeyDown(Keys.LeftControl) || Input.IsKeyDown(Keys.RightControl) || ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeySuper;
            _shiftDown = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift) || ImGui.GetIO().KeyShift;
        }

        private void HandleBoxSelection()
        {
            mouseScreen = Core.Scene.Camera.ScreenToWorldPoint(Input.ScaledMousePosition);

            if (!_isBoxSelecting && Input.LeftMouseButtonPressed)
            {
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

        private RectangleF GetBoxSelectionRectangle(Vector2 worldStart, Vector2 worldEnd)
        {
            var camera = Core.Scene.Camera;
            var screenStart = camera.WorldToScreenPoint(worldStart);
            var screenEnd = camera.WorldToScreenPoint(worldEnd);

            var min = new Vector2(Math.Min(screenStart.X, screenEnd.X), Math.Min(screenStart.Y, screenEnd.Y));
            var max = new Vector2(Math.Max(screenStart.X, screenEnd.X), Math.Max(screenStart.Y, screenEnd.Y));

            return new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y);
        }

        private void DrawSelectionBoxNez(Vector2 worldStart, Vector2 worldEnd)
        {
            Debug.DrawRect(GetBoxSelectionRectangle(worldStart, worldEnd), Color.CornflowerBlue * 0.7f, 0f);
        }

        private void SelectEntitiesInBox(Vector2 worldStart, Vector2 worldEnd)
        {
            var selectionRect = GetBoxSelectionRectangle(worldStart, worldEnd);
            var selectedEntities = new List<Entity>();

            for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
            {
                var entity = Core.Scene.Entities[i];

                var sprite = entity.GetComponent<SpriteRenderer>();
                var collider = entity.GetComponent<Collider>();
                if (sprite == null && collider == null)
                    continue;

                RectangleF entityBounds = GetEntityBounds(entity);

                if (entityBounds.Width <= 0 || entityBounds.Height <= 0)
                    continue;

                float intersectX = Math.Max(selectionRect.X, entityBounds.X);
                float intersectY = Math.Max(selectionRect.Y, entityBounds.Y);
                float intersectRight = Math.Min(selectionRect.X + selectionRect.Width, entityBounds.X + entityBounds.Width);
                float intersectBottom = Math.Min(selectionRect.Y + selectionRect.Height, entityBounds.Y + entityBounds.Height);

                float intersectWidth = intersectRight - intersectX;
                float intersectHeight = intersectBottom - intersectY;

                if (intersectWidth > 0 && intersectHeight > 0)
                {
                    float intersectionArea = intersectWidth * intersectHeight;
                    float entityArea = entityBounds.Width * entityBounds.Height;
                    float coverage = intersectionArea / entityArea;

                    if (coverage >= 0.7f)
                    {
                        if (sprite != null && !sprite.IsSelectableInEditor)
                            continue;

                        selectedEntities.Add(entity);
                    }
                }
            }

            if (selectedEntities.Count > 0)
            {
                var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
                entityPane.DeselectAllEntities();
                foreach (var entity in selectedEntities)
                    entityPane.SetSelectedEntity(entity, _ctrlDown, true);

                _imGuiManager.OpenMainEntityInspector(selectedEntities[0]);
                SetCameraTargetPosition(selectedEntities[0].Transform.Position);
            }
        }

        private RectangleF GetEntityBounds(Entity entity)
        {
            var collider = entity.GetComponent<Collider>();
            if (collider != null)
                return collider.Bounds;

            var sprite = entity.GetComponent<SpriteRenderer>();
            if (sprite != null)
                return sprite.Bounds;

            var pos = entity.Transform.Position;
            return new RectangleF(pos.X - 8, pos.Y - 8, 16, 16);
        }

        private bool TrySelectEntityAtMouse()
        {
            var mouseWorld = Core.Scene.Camera.ScreenToWorldPoint(Input.ScaledMousePosition);
            Entity selected = null;

            for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
            {
                var entity = Core.Scene.Entities[i];
                var collider = entity.GetComponent<Collider>();
                if (collider != null && collider.Bounds.Contains(mouseWorld))
                {
                    selected = entity;
                    break;
                }
            }

            if (selected == null)
            {
                float minDist = 16f;
                for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
                {
                    var entity = Core.Scene.Entities[i];
                    var sprite = entity.GetComponent<SpriteRenderer>();
                    if (sprite != null)
                    {
                        if (!sprite.IsSelectableInEditor)
                            continue;

                        var bounds = sprite.Bounds;
                        if (bounds.Contains(mouseWorld))
                        {
                            selected = entity;
                            break;
                        }
                    }
                    else
                    {
                        float dist = Vector2.Distance(entity.Transform.Position, mouseWorld);
                        if (dist < minDist)
                        {
                            selected = entity;
                            minDist = dist;
                        }
                    }
                }
            }

            if (selected != null)
            {
                _imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(selected, _ctrlDown, _shiftDown);
                _imGuiManager.OpenMainEntityInspector(selected);
                SetCameraTargetPosition(selected.Transform.Position);
                return true;
            }
            return false;
        }

        public void DeselectEntity()
        {
            if (_imGuiManager.SceneGraphWindow?.EntityPane != null)
                _imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(null, false);
        }

        public void SetCameraTargetPosition(Vector2 position)
        {
            _imGuiManager.CameraTargetPosition = _imGuiManager.SceneGraphWindow.EntityPane.GetSelectedEntitiesCenter();
        }

        /// <summary>
        /// Draws the X/Y axis arrows for the selected entity and handles axis hover.
        /// </summary>
        private void DrawAndHandleEntityGizmo()
        {
            var entityPane = _imGuiManager.SceneGraphWindow.EntityPane;
            var selectedEntities = entityPane.SelectedEntities;
            IsMouseOverGizmo = false;

            if (selectedEntities.Count == 0 || !Core.IsEditMode)
                return;

            Vector2 center = Vector2.Zero;
            foreach (var e in selectedEntities)
                center += e.Transform.Position;
            center /= selectedEntities.Count;

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

            // Axis hover and drag logic is handled in HandleEntityDragging()
        }

        /// <summary>
        /// Utility to check if mouse is near a line segment.
        /// </summary>
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

            // Compute gizmo axis positions
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
                        _dragStartEntityPositions[entity] = entity.Transform.Position;

                    _dragStartWorldMouse = camera.ScreenToWorldPoint(mousePos);
                }
            }

            // Dragging
            if ((_draggingX || _draggingY) && Input.LeftMouseButtonDown)
            {
                var delta = worldMouse - _dragStartWorldMouse;
                foreach (var entity in selectedEntities)
                {
                    var startPos = _dragStartEntityPositions.TryGetValue(entity, out var pos) ? pos : entity.Transform.Position;
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
                    _dragEndEntityPositions[entity] = entity.Transform.Position;

                // Only push undo if any entity moved
                bool anyMoved = selectedEntities.Any(e => _dragStartEntityPositions[e] != _dragEndEntityPositions[e]);
                if (anyMoved)
                {
                    EditorChangeTracker.PushUndo(
                        new MultiEntityTransformUndoAction(
                            selectedEntities.ToList(),
                            _dragStartEntityPositions,
                            _dragEndEntityPositions,
                            $"Move {string.Join(", ", selectedEntities.Select(e => e.Name))}"
                        ),
                        selectedEntities.First(),
                        $"Move {string.Join(", ", selectedEntities.Select(e => e.Name))}"
                    );
                }
            }
        }
    }
}