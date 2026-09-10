using System.Collections.Generic;

namespace Voltage.Aseprite;

/// <summary>
/// Represents the contents loaded from an Aseprite file.
/// </summary>
public sealed partial class AsepriteFile
{
	/// <summary>
	/// The width, in pixels, defined for the canvas of the Aseprite image.
	/// </summary>
	/// <remarks>
	/// This is also the width of every frame.
	/// </remarks>
	public readonly int CanvasWidth;

	/// <summary>
	/// The height, in pixels, defined for the canvas of the Aseprite image.
	/// </summary>
	/// <remarks>
	/// This is also the height of every frame.
	/// </remarks>
	public readonly int CanvasHeight;

	/// <summary>
	/// The color depth mode used for the image in Aseprite which defines the total number of bits per pixel.
	/// </summary>
	public readonly AsepriteColorDepth ColorDepth;

	/// <summary>
	/// A collection of all frame elements in the Aseprite file.  Order of elements is from first-to-last.
	/// </summary>
	public readonly List<AsepriteFrame> Frames;

	/// <summary>
	/// A collection of all layer elements in the Aseprite file.  Order of elements is from bottom-to-top.
	/// </summary>
	public readonly List<AsepriteLayer> Layers;

	/// <summary>
	/// A collection of all tag elements from the Aseprite file.  Order of elements is as defined in the Aseprite UI
	/// from left-to-right.
	/// </summary>
	public readonly List<AsepriteTag> Tags;

	/// <summary>
	/// A collection of all slice elements from the Aseprite file.  Order of elements is in the order they were
	/// created in Aseprite.
	/// </summary>
	public readonly List<AsepriteSlice> Slices;

	/// <summary>
	/// A collection of any warnings issued when parsing the Aseprite file.  You can use this to see if there were
	/// any non-fatal errors that occurred while parsing the file.
	/// </summary>
	public readonly List<string> Warnings;

	/// <summary>
	/// The palette data from the Aseprite file containing the palette information and colors.
	/// </summary>
	public readonly AsepritePalette Palette;

	/// <summary>
	/// The custom user data that was set in the sprite properties in Aseprite.
	/// </summary>
	public AsepriteUserData UserData { get; }

	/// <summary>
	/// The name of this Aseprite file (without extension)
	/// </summary>
	public readonly string Name;

	internal AsepriteFile(string name, AsepritePalette palette, int width, int height, AsepriteColorDepth colorDepth,
		List<AsepriteFrame> frames, List<AsepriteLayer> layers, List<AsepriteTag> tags, List<AsepriteSlice> slices,
		List<string> warnings)
	{
		Name = name;
		CanvasWidth = width;
		CanvasHeight = height;
		ColorDepth = colorDepth;
		Frames = frames;
		Layers = layers;
		Tags = tags;
		Slices = slices;
		Warnings = warnings;
		Palette = palette;
	}
}
