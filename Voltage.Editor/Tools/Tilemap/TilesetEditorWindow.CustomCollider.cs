using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Tilesets;
using Num = System.Numerics;

namespace Voltage.Editor.Tools.Tilemap
{
	/// <summary>Collision-shape preview overlay and the modal polygon editor for reusable named custom colliders.</summary>
	public partial class TilesetEditorWindow
	{
		private const string ColliderPopupId = "tileset-custom-collider";
		private const float HandleGrabRadius = 10f;
		private const float ColliderCanvasSide = 360f;

		private bool _colliderPopupRequestOpen;
		private string _colliderName = string.Empty;
		private List<Num.Vector2> _colliderPoints = new();
		private int _colliderDragIndex = -1;
		private int _colliderTileForPreview = -1;
		private TilesetTileInfo _colliderTargetTile;
		private string _colliderEditingOriginalName;

		#region Preview overlay

		/// <summary>Draws every tile's collision shape on the atlas (selected tile bright, the rest faint) so it reads in place.</summary>
		private void DrawCollisionOverlay(Num.Vector2 origin)
		{
			if (_preview?.Asset == null)
				return;

			var drawList = ImGui.GetWindowDrawList();
			var tw = _preview.TileWidth;
			var th = _preview.TileHeight;
			var spacing = _preview.Asset.Spacing;
			var margin = _preview.Asset.Margin;

			for (var i = 0; i < _preview.TileCount; i++)
			{
				var info = _preview.Asset.GetTileInfo(i);
				if (info == null || info.CollisionShape == TileCollisionShape.None)
					continue;

				var isSelected = i == _selectedTile;

				// Box is the implicit default; only outline it on the selected tile.
				if (info.CollisionShape == TileCollisionShape.Box && !isSelected)
					continue;

				var col = i % _preview.Columns;
				var row = i / _preview.Columns;
				var topLeft = new Num.Vector2(
					origin.X + (margin + col * (tw + spacing)) * _zoom,
					origin.Y + (margin + row * (th + spacing)) * _zoom);

				DrawShapeOutline(drawList, info, topLeft, tw, th, isSelected);
			}
		}

		private void DrawShapeOutline(ImDrawListPtr drawList, TilesetTileInfo info, Num.Vector2 topLeft,
			int tw, int th, bool selected)
		{
			var line = ImGui.GetColorU32(new Num.Vector4(0.3f, 1f, 0.45f, selected ? 1f : 0.55f));
			var fill = ImGui.GetColorU32(new Num.Vector4(0.3f, 1f, 0.45f, selected ? 0.22f : 0.12f));
			var thickness = selected ? 2f : 1.25f;

			if (info.CollisionShape == TileCollisionShape.Circle)
			{
				var center = new Num.Vector2(topLeft.X + tw * 0.5f * _zoom, topLeft.Y + th * 0.5f * _zoom);
				var radius = Math.Min(tw, th) * 0.5f * _zoom;
				drawList.AddCircleFilled(center, radius, fill, 32);
				drawList.AddCircle(center, radius, line, 32, thickness);
				return;
			}

			var pts = CollisionOutlineNormalized(info);
			if (pts == null || pts.Count < 2)
				return;

			var screen = new Num.Vector2[pts.Count];
			for (var i = 0; i < screen.Length; i++)
				screen[i] = new Num.Vector2(topLeft.X + pts[i].X * tw * _zoom, topLeft.Y + pts[i].Y * th * _zoom);

			if (screen.Length >= 3)
				drawList.AddConvexPolyFilled(ref screen[0], screen.Length, fill);

			for (var i = 0; i < screen.Length; i++)
				drawList.AddLine(screen[i], screen[(i + 1) % screen.Length], line, thickness);
		}

		/// <summary>Outline points in normalized tile space (0..1) for box/slope/custom shapes; null for none/circle.</summary>
		private List<Num.Vector2> CollisionOutlineNormalized(TilesetTileInfo info)
		{
			switch (info.CollisionShape)
			{
				case TileCollisionShape.Box:
					return new List<Num.Vector2> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };

				case TileCollisionShape.SlopeUpRight:
					return new List<Num.Vector2> { new(0, 1), new(1, 0), new(1, 1) };
				case TileCollisionShape.SlopeUpLeft:
					return new List<Num.Vector2> { new(0, 0), new(1, 1), new(0, 1) };
				case TileCollisionShape.SlopeDownRight:
					return new List<Num.Vector2> { new(0, 0), new(1, 0), new(0, 1) };
				case TileCollisionShape.SlopeDownLeft:
					return new List<Num.Vector2> { new(0, 0), new(1, 0), new(1, 1) };

				case TileCollisionShape.Custom:
					var custom = _asset.GetCustomCollider(info.CustomColliderName);
					if (custom?.Points == null || custom.Points.Count < 2)
						return null;

					var list = new List<Num.Vector2>(custom.Points.Count);
					foreach (var p in custom.Points)
						list.Add(new Num.Vector2(p.X, p.Y));
					return list;

				default:
					return null;
			}
		}

		#endregion

		#region Custom collider editor

		private void OpenColliderEditor(TilesetTileInfo tile, string existingName)
		{
			_colliderTargetTile = tile;
			_colliderTileForPreview = tile?.Index ?? _selectedTile;
			_colliderDragIndex = -1;

			var existing = _asset.GetCustomCollider(existingName);
			if (existing != null)
			{
				_colliderEditingOriginalName = existing.Name;
				_colliderName = existing.Name ?? string.Empty;
				_colliderPoints = new List<Num.Vector2>();
				foreach (var p in existing.Points)
					_colliderPoints.Add(new Num.Vector2(p.X, p.Y));

				if (_colliderPoints.Count < 3)
					SetDefaultColliderPoints();
			}
			else
			{
				_colliderEditingOriginalName = null;
				_colliderName = UniqueColliderName();
				SetDefaultColliderPoints();
			}

			_colliderPopupRequestOpen = true;
		}

		private void DrawColliderPopup()
		{
			if (_colliderPopupRequestOpen)
			{
				ImGui.OpenPopup(ColliderPopupId);
				_colliderPopupRequestOpen = false;
			}

			var center = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(440, 560), ImGuiCond.Appearing);

			var open = true;
			if (!Gui.BeginPopupModal(ColliderPopupId, ref open, ImGuiWindowFlags.NoResize))
				return;

			ImGui.TextUnformatted("Name");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(-1);
			Gui.InputText("##collidername", ref _colliderName, 64);

			DrawColliderCanvas();

			if (Gui.Button("Add vertex"))
				AddColliderVertex();

			ImGui.SameLine();
			ImGui.BeginDisabled(_colliderDragIndex < 0 || _colliderPoints.Count <= 3);
			if (Gui.Button("Remove vertex"))
				RemoveColliderVertex();
			ImGui.EndDisabled();

			ImGui.SameLine();
			ImGui.TextDisabled($"{_colliderPoints.Count} points");

			ImGui.Separator();

			var canSave = !string.IsNullOrWhiteSpace(_colliderName) && _colliderPoints.Count >= 3;
			ImGui.BeginDisabled(!canSave);
			if (Gui.Button("Save", new Num.Vector2(120, 0)))
			{
				SaveColliderEditor();
				ImGui.CloseCurrentPopup();
			}
			ImGui.EndDisabled();

			ImGui.SameLine();
			if (Gui.Button("Cancel", new Num.Vector2(120, 0)))
				ImGui.CloseCurrentPopup();

			Gui.EndPopup();
		}

		private void DrawColliderCanvas()
		{
			var side = ColliderCanvasSide;
			var canvasPos = ImGui.GetCursorScreenPos();
			var canvasSize = new Num.Vector2(side, side);

			Gui.InvisibleButton("collider-canvas", canvasSize);
			var active = ImGui.IsItemActive();
			var mouse = ImGui.GetIO().MousePos;

			var drawList = ImGui.GetWindowDrawList();
			drawList.AddRectFilled(canvasPos, canvasPos + canvasSize,
				ImGui.GetColorU32(new Num.Vector4(0.1f, 0.1f, 0.1f, 1f)));

			if (_previewTextureId != IntPtr.Zero && _preview != null && _preview.IsValidIndex(_colliderTileForPreview))
			{
				var rect = _preview.SourceRects[_colliderTileForPreview];
				var texW = (float)_preview.Texture.Width;
				var texH = (float)_preview.Texture.Height;
				var uv0 = new Num.Vector2(rect.X / texW, rect.Y / texH);
				var uv1 = new Num.Vector2((rect.X + rect.Width) / texW, (rect.Y + rect.Height) / texH);
				drawList.AddImage(_previewTextureId, canvasPos, canvasPos + canvasSize, uv0, uv1);
			}

			drawList.AddRect(canvasPos, canvasPos + canvasSize,
				ImGui.GetColorU32(new Num.Vector4(0.5f, 0.5f, 0.5f, 1f)));

			Num.Vector2 ToScreen(Num.Vector2 n) => new(canvasPos.X + n.X * side, canvasPos.Y + n.Y * side);

			// Pick the nearest handle on press, drag it while held.
			if (active && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				_colliderDragIndex = -1;
				var best = HandleGrabRadius * HandleGrabRadius;
				for (var i = 0; i < _colliderPoints.Count; i++)
				{
					var s = ToScreen(_colliderPoints[i]);
					var dx = s.X - mouse.X;
					var dy = s.Y - mouse.Y;
					var d2 = dx * dx + dy * dy;
					if (d2 <= best)
					{
						best = d2;
						_colliderDragIndex = i;
					}
				}
			}

			if (active && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _colliderDragIndex >= 0)
			{
				_colliderPoints[_colliderDragIndex] = new Num.Vector2(
					Math.Clamp((mouse.X - canvasPos.X) / side, 0f, 1f),
					Math.Clamp((mouse.Y - canvasPos.Y) / side, 0f, 1f));
			}

			var edge = ImGui.GetColorU32(new Num.Vector4(0.3f, 1f, 0.45f, 1f));
			if (_colliderPoints.Count >= 3)
			{
				var poly = new Num.Vector2[_colliderPoints.Count];
				for (var i = 0; i < poly.Length; i++)
					poly[i] = ToScreen(_colliderPoints[i]);
				drawList.AddConvexPolyFilled(ref poly[0], poly.Length,
					ImGui.GetColorU32(new Num.Vector4(0.3f, 1f, 0.45f, 0.20f)));
			}

			for (var i = 0; i < _colliderPoints.Count; i++)
			{
				var a = ToScreen(_colliderPoints[i]);
				var b = ToScreen(_colliderPoints[(i + 1) % _colliderPoints.Count]);
				drawList.AddLine(a, b, edge, 2f);
			}

			for (var i = 0; i < _colliderPoints.Count; i++)
			{
				var s = ToScreen(_colliderPoints[i]);
				var selected = i == _colliderDragIndex;
				var handle = selected
					? ImGui.GetColorU32(new Num.Vector4(1f, 1f, 0.2f, 1f))
					: ImGui.GetColorU32(new Num.Vector4(0.2f, 0.8f, 1f, 1f));
				drawList.AddCircleFilled(s, selected ? 6f : 5f, handle);
			}

			ImGui.TextDisabled("Drag points to shape the collider. Coordinates are tile-relative, so it reuses at any tile size.");
		}

		private void SetDefaultColliderPoints()
		{
			_colliderPoints = new List<Num.Vector2>
			{
				new(0.15f, 0.15f), new(0.85f, 0.15f), new(0.85f, 0.85f), new(0.15f, 0.85f),
			};
			_colliderDragIndex = -1;
		}

		private string UniqueColliderName()
		{
			var n = _asset.CustomColliders.Count + 1;
			string name;
			do
			{
				name = $"Collider {n++}";
			} while (_asset.GetCustomCollider(name) != null);

			return name;
		}

		private void AddColliderVertex()
		{
			if (_colliderPoints.Count < 2)
			{
				_colliderPoints.Add(new Num.Vector2(0.5f, 0.5f));
				return;
			}

			// Split the longest edge.
			var bestLen = -1f;
			var bestIndex = 0;
			for (var i = 0; i < _colliderPoints.Count; i++)
			{
				var a = _colliderPoints[i];
				var b = _colliderPoints[(i + 1) % _colliderPoints.Count];
				var dx = a.X - b.X;
				var dy = a.Y - b.Y;
				var len = dx * dx + dy * dy;
				if (len > bestLen)
				{
					bestLen = len;
					bestIndex = i;
				}
			}

			var p = _colliderPoints[bestIndex];
			var q = _colliderPoints[(bestIndex + 1) % _colliderPoints.Count];
			_colliderPoints.Insert(bestIndex + 1, new Num.Vector2((p.X + q.X) * 0.5f, (p.Y + q.Y) * 0.5f));
			_colliderDragIndex = bestIndex + 1;
		}

		private void RemoveColliderVertex()
		{
			if (_colliderDragIndex >= 0 && _colliderDragIndex < _colliderPoints.Count && _colliderPoints.Count > 3)
			{
				_colliderPoints.RemoveAt(_colliderDragIndex);
				_colliderDragIndex = -1;
			}
		}

		private void SaveColliderEditor()
		{
			var name = _colliderName?.Trim();
			if (string.IsNullOrEmpty(name) || _colliderPoints.Count < 3)
				return;

			// Normalize winding to clockwise, else the collider is inverted.
			var ordered = new List<Num.Vector2>(_colliderPoints);
			if (SignedArea(ordered) < 0f)
				ordered.Reverse();

			var pts = new List<Microsoft.Xna.Framework.Vector2>(ordered.Count);
			foreach (var p in ordered)
				pts.Add(new Microsoft.Xna.Framework.Vector2(p.X, p.Y));

			var target = _asset.GetCustomCollider(_colliderEditingOriginalName) ?? _asset.GetCustomCollider(name);
			if (target != null)
			{
				var oldName = target.Name;
				target.Name = name;
				target.Points = pts;

				if (!string.IsNullOrEmpty(oldName) && oldName != name)
					RenameColliderReferences(oldName, name);
			}
			else
			{
				_asset.CustomColliders.Add(new TilesetCollider { Name = name, Points = pts });
			}

			if (_colliderTargetTile != null)
				_colliderTargetTile.CustomColliderName = name;

			_dirty = true;
		}

		// Shoelace area (Y-down); positive = clockwise.
		private static float SignedArea(List<Num.Vector2> pts)
		{
			var sum = 0f;
			for (var i = 0; i < pts.Count; i++)
			{
				var a = pts[i];
				var b = pts[(i + 1) % pts.Count];
				sum += a.X * b.Y - b.X * a.Y;
			}

			return sum * 0.5f;
		}

		private void RenameColliderReferences(string oldName, string newName)
		{
			foreach (var t in _asset.Tiles)
			{
				if (t.CustomColliderName == oldName)
					t.CustomColliderName = newName;
			}
		}

		#endregion
	}
}
