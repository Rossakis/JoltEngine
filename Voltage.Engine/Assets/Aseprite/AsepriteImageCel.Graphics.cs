using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Textures;

namespace Voltage.Aseprite
{
	public sealed partial class AsepriteImageCel
	{
		/// <summary>
		/// Translates the pixel data of this cel into a new sprite instance.
		/// </summary>
		/// <returns>
		/// A new instance of the <see cref="Sprite"/> class initialized with a texture generated from the pixel data
		/// of this cel.
		/// </returns>
		public Sprite ToSprite()
		{
			Texture2D texture = new Texture2D(Core.GraphicsDevice, Width, Height);
			texture.SetData<Color>(Pixels);
			return new Sprite(texture);
		}
	}
}
