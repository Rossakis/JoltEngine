using Microsoft.Xna.Framework;

namespace Voltage.Aseprite
{
	/// <summary>
	/// Represents a single cel in a frame in Aseprite that contains image data.
	/// </summary>
	public sealed partial class AsepriteImageCel : AsepriteCel
	{
		/// <summary>
		/// The width, in pixels, of this cel.
		/// </summary>
		public int Width;

		/// <summary>
		/// The height, in pixels, of this cel.
		/// </summary>
		public int Height;

		/// <summary>
		/// An array of color elements that represents the pixel data that makes up the image for this cel.  Order of
		/// the color elements starts with the top-left most pixel and is read left-to-right from top-to-bottom.
		/// </summary>
		public readonly Color[] Pixels;

		internal AsepriteImageCel(int width, int height, Color[] pixels, AsepriteLayer layer, Point position, int opacity)
			: base(layer, position, opacity)
		{
			Width = width;
			Height = height;
			Pixels = pixels;
		}
	}
}
