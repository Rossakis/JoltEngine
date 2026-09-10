using System.ComponentModel;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;

namespace Voltage.Pipeline.Fonts;

/// <summary>Builds each page image as its own texture asset the font references at load time.</summary>
[ContentProcessor(DisplayName = "BMFont - Voltage")]
public sealed class BitmapFontProcessor : ContentProcessor<BitmapFontContent, BitmapFontContent>
{
	/// <summary>Premultiply the page textures at build time; the engine premultiplies on the CPU when a loader asks for it.</summary>
	[DefaultValue(false)]
	public bool PremultiplyAlpha { get; set; }

	public override BitmapFontContent Process(BitmapFontContent input, ContentProcessorContext context)
	{
		var parameters = new OpaqueDataDictionary
		{
			{ "PremultiplyAlpha", PremultiplyAlpha },
			{ "GenerateMipmaps", false },
			{ "ColorKeyEnabled", false },
			{ "TextureFormat", TextureProcessorOutputFormat.Color }
		};

		// Pages sit beside the font's own output name, so a font at Fonts/Hud yields Fonts/Hud_0.
		var fontName = System.IO.Path.ChangeExtension(System.IO.Path.GetRelativePath(context.OutputDirectory, context.OutputFilename), null).Replace('\\', '/');
		foreach (var page in input.Pages)
			page.Texture = context.BuildAsset<TextureContent, TextureContent>(new ExternalReference<TextureContent>(page.File), "VoltageTextureProcessor", parameters, "TextureImporter", $"{fontName}_{page.Id}");

		return input;
	}
}
