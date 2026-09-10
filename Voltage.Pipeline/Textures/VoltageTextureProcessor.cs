using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;

namespace Voltage.Pipeline.Textures;

/// <summary>The stock texture processor plus the blackout of alpha-zero texels that Texture2D.FromStream applies, so a compiled texture matches a raw load pixel for pixel.</summary>
[ContentProcessor(DisplayName = "Texture - Voltage")]
public sealed class VoltageTextureProcessor : TextureProcessor
{
	public VoltageTextureProcessor()
	{
		PremultiplyAlpha = false;
		GenerateMipmaps = false;
		ColorKeyEnabled = false;
		ResizeToPowerOfTwo = false;
		TextureFormat = TextureProcessorOutputFormat.Color;
	}

	public override TextureContent Process(TextureContent input, ContentProcessorContext context)
	{
		var output = base.Process(input, context);
		if (PremultiplyAlpha || TextureFormat != TextureProcessorOutputFormat.Color)
			return output;

		foreach (var face in output.Faces)
			foreach (var mip in face)
				if (mip is PixelBitmapContent<Color> bitmap)
					Blackout(bitmap);
		return output;
	}

	private static void Blackout(PixelBitmapContent<Color> bitmap)
	{
		for (var y = 0; y < bitmap.Height; y++)
		{
			var row = bitmap.GetRow(y);
			for (var x = 0; x < row.Length; x++)
				if (row[x].A == 0 && row[x].PackedValue != 0)
					bitmap.SetPixel(x, y, Color.Transparent);
		}
	}
}
