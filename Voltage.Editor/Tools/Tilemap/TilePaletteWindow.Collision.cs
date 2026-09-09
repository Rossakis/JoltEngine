using System.Collections.Generic;
using ImGuiNET;
using Voltage.Editor.Undo.Core;
using Voltage.Project;
using Num = System.Numerics;

namespace Voltage.Editor.Tools.Tilemap
{
	public partial class TilePaletteWindow
	{
		private void DrawEditModeSwitch(TilePaintTool tool)
		{
			ImGui.TextUnformatted("Mode");
			ImGui.SameLine();

			DrawModeButton(tool, TileEditMode.Tiles, "Tiles", "Paint tiles.");
			ImGui.SameLine();
			DrawModeButton(tool, TileEditMode.Collision, "Collision", "Paint the collision mask, then generate colliders from it.");
		}

		private static void DrawModeButton(TilePaintTool tool, TileEditMode mode, string label, string tooltip)
		{
			var active = tool.EditMode == mode;
			if (active)
				ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0.85f, 0.35f, 0.2f, 1f));

			if (Gui.Button(label))
				tool.EditMode = mode;

			if (active)
				ImGui.PopStyleColor();

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(tooltip);
		}

		/// <summary>Collision-mode panel: brush the solid mask, set the physics layers, generate/rebuild colliders.</summary>
		private void DrawCollisionControls(TilePaintTool tool)
		{
			var map = tool.Target;

			if (map?.Entity == null)
			{
				ImGui.TextDisabled("Select or create a layer to edit its collision.");
				return;
			}

			ImGui.TextColored(new Num.Vector4(1f, 0.55f, 0.4f, 1f),
				$"Painting collision on '{map.Entity.Name}'.");
			ImGui.TextDisabled($"{map.CollisionCellCount} solid cells -> {map.GetCollisionRects().Count} colliders.");

			ImGui.Spacing();

			var physicsLayer = map.PhysicsLayer;
			if (DrawPhysicsLayerCombo("Physics layer", ref physicsLayer))
			{
				map.PhysicsLayer = physicsLayer;
				map.RebuildColliders();
				EditorChangeTracker.MarkChanged(map.Entity, "Tilemap physics layer");
			}

			var mask = map.CollidesWithLayers;
			if (DrawPhysicsLayerMask("Collides with", ref mask))
			{
				map.CollidesWithLayers = mask;
				map.RebuildColliders();
				EditorChangeTracker.MarkChanged(map.Entity, "Tilemap collision mask");
			}

			var isTrigger = map.IsTrigger;
			if (Gui.Checkbox("Is trigger", ref isTrigger))
			{
				map.IsTrigger = isTrigger;
				map.RebuildColliders();
				EditorChangeTracker.MarkChanged(map.Entity, "Tilemap trigger");
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Triggers fire overlap events but do not block movement.");

			var autoBuild = map.AutoBuildColliders;
			if (Gui.Checkbox("Build colliders on start", ref autoBuild))
			{
				map.AutoBuildColliders = autoBuild;
				EditorChangeTracker.MarkChanged(map.Entity, "Tilemap auto-build colliders");
			}

			ImGui.Spacing();
			ImGui.Separator();
			ImGui.TextUnformatted("Generate");

			if (Gui.Button("Solid from all tiles"))
			{
				var added = map.GenerateCollisionFromTiles(onlyTilesetSolidFlagged: false);
				map.RebuildColliders();
				EditorChangeTracker.MarkChanged(map.Entity, $"Collision from all tiles (+{added})");
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Mark every painted cell as solid - one massive merged collider set for the whole layer.");

			ImGui.SameLine();

			if (Gui.Button("Solid from flagged tiles"))
			{
				var added = map.GenerateCollisionFromTiles(onlyTilesetSolidFlagged: true);
				map.RebuildColliders();
				EditorChangeTracker.MarkChanged(map.Entity, $"Collision from flagged tiles (+{added})");
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Mark only cells whose tile is flagged Solid in the tileset (set the flag in the Tileset Editor).");

			if (Gui.Button("Rebuild colliders"))
				map.RebuildColliders();

			ImGui.SameLine();

			if (Gui.Button("Clear collision"))
			{
				map.ClearCollision();
				map.RebuildColliders();
				EditorChangeTracker.MarkChanged(map.Entity, "Clear tilemap collision");
			}

			ImGui.Spacing();
			ImGui.TextDisabled("Brush / Rect / Fill paint the mask. Shift or right-drag erases.");
		}

		// Physics layers are named in ProjectSettings; mirror the PhysicsLayer / PhysicsLayerMask inspectors.
		private static bool DrawPhysicsLayerCombo(string label, ref int layerBit)
		{
			GetPhysicsLayers(out var names, out var bits);
			if (names.Count == 0)
			{
				ImGui.TextDisabled("No physics layers defined in Project Settings.");
				return false;
			}

			var current = 0;
			for (var i = 0; i < bits.Count; i++)
			{
				if (bits[i] == layerBit)
				{
					current = i;
					break;
				}
			}

			ImGui.SetNextItemWidth(180f);
			if (Gui.Combo(label, ref current, names.ToArray(), names.Count))
			{
				layerBit = bits[current];
				return true;
			}

			return false;
		}

		private static bool DrawPhysicsLayerMask(string label, ref int mask)
		{
			GetPhysicsLayers(out var names, out var bits);
			if (names.Count == 0)
				return false;

			var changed = false;

			if (Gui.TreeNode($"{label}: {DescribeMask(mask, names, bits)}"))
			{
				for (var i = 0; i < names.Count; i++)
				{
					var isChecked = mask == Physics.AllLayers || (mask & bits[i]) != 0;
					if (Gui.Checkbox(names[i], ref isChecked))
					{
						// Materialise "all layers" (-1) into concrete bits before clearing one, or the unset
						// would leave every other layer still implicitly set.
						if (mask == Physics.AllLayers)
						{
							mask = 0;
							for (var j = 0; j < bits.Count; j++)
								mask |= bits[j];
						}

						if (isChecked) mask |= bits[i];
						else mask &= ~bits[i];

						changed = true;
					}
				}

				ImGui.TreePop();
			}

			return changed;
		}

		private static string DescribeMask(int mask, List<string> names, List<int> bits)
		{
			if (mask == Physics.AllLayers)
				return "All";

			var set = new List<string>();
			for (var i = 0; i < names.Count; i++)
			{
				if ((mask & bits[i]) != 0)
					set.Add(names[i]);
			}

			return set.Count == 0 ? "None" : string.Join(", ", set);
		}

		private static void GetPhysicsLayers(out List<string> names, out List<int> bits)
		{
			names = new List<string>();
			bits = new List<int>();

			foreach (var kvp in ProjectSettings.Instance.Physics.PhysicsLayers)
			{
				names.Add(kvp.Key);
				bits.Add(1 << kvp.Value);
			}
		}
	}
}
