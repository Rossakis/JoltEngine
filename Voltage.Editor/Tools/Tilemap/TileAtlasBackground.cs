using System;
using ImGuiNET;
using Voltage.Editor.Persistence;
using Num = System.Numerics;

namespace Voltage.Editor.Tools.Tilemap
{
	/// <summary>
	/// The backdrop drawn behind a tile atlas, shared by the Tile Palette and the Tileset Editor so both show the
	/// same colour and one picker changes both. A viewing aid only - tiles keep their real transparency when painted.
	/// </summary>
	public static class TileAtlasBackground
	{
		// Packed 0xAARRGGBB. The key is the palette's original one, so a colour picked before this was shared carries over.
		private const int Default = unchecked((int)0xFF1E1E1E);

		private static readonly PersistentInt _packed = new("TilePalette_AtlasBackground", Default);

		public static Num.Vector4 Color => Unpack(_packed.Value);

		public static uint ColorU32 => ImGui.GetColorU32(Color);

		/// <summary>Label, swatch and reset. Draws on its own line - callers should not SameLine it.</summary>
		public static void DrawPicker()
		{
			var color = Color;

			// Grouped so the tooltip covers the label as well as the swatch.
			ImGui.BeginGroup();

			ImGui.TextUnformatted("Background Color");
			ImGui.SameLine();

			if (Gui.ColorEdit4("##atlasbg", ref color,
				    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel |
				    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview))
			{
				_packed.Value = Pack(color);
			}

			ImGui.EndGroup();

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Preview backdrop only.\n" +
				                 "The tiles you place are unaffected.");
			}

			ImGui.SameLine();
			if (Gui.SmallButton("Reset bg"))
				_packed.Value = Default;
		}

		private static Num.Vector4 Unpack(int packed) => new(
			((packed >> 16) & 0xFF) / 255f,
			((packed >> 8) & 0xFF) / 255f,
			(packed & 0xFF) / 255f,
			((packed >> 24) & 0xFF) / 255f);

		private static int Pack(Num.Vector4 color)
		{
			static int Channel(float v) => (int)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

			return (Channel(color.W) << 24) | (Channel(color.X) << 16) | (Channel(color.Y) << 8) | Channel(color.Z);
		}
	}
}
