using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Textures;

namespace Voltage.Aseprite
{
	public sealed partial class AsepriteFrame
	{
		/// <summary>
		/// Translates the data in this frame into a sprite.
		/// </summary>
		/// <param name="onlyVisibleLayers">
		/// Indicates whether only cels that are on visible layers should be included when flattening this frame.
		/// </param>
		/// <param name="includeBackgroundLayer">
		/// Indicates whether the cel on the layer marked as the background layer in Aseprite should be included when
		/// flattening this frame.
		/// </param>
		/// <returns>
		/// A new instance of the <see cref="Sprite"/> class initialized by the image data in this frame.
		/// </returns>
		public Sprite ToSprite(bool onlyVisibleLayers = true, bool includeBackgroundLayer = false)
		{
			Color[] pixels = FlattenFrame(onlyVisibleLayers, includeBackgroundLayer);
			Texture2D texture = new Texture2D(Core.GraphicsDevice, Width, Height);
			texture.SetData<Color>(pixels);
			return new Sprite(texture);
		}
	}
}
