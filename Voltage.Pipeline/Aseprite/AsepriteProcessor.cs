using System.ComponentModel;
using Microsoft.Xna.Framework.Content.Pipeline;
using Voltage.Aseprite;

namespace Voltage.Pipeline.Aseprite;

/// <summary>Keeps every cel as raw RGBA at its own bounds, which is the per-frame, per-layer form the runtime composites from; only the colour conversion moves to build time.</summary>
[ContentProcessor(DisplayName = "Aseprite - Voltage")]
public sealed class AsepriteProcessor : ContentProcessor<AsepriteContent, AsepriteContent>
{
	/// <summary>Premultiply every cel at build time; leave off to match the engine's raw loader.</summary>
	[DefaultValue(false)]
	public bool PremultiplyAlpha { get; set; }

	public override AsepriteContent Process(AsepriteContent input, ContentProcessorContext context)
	{
		if (!PremultiplyAlpha)
			return input;

		var premultiplied = AsepriteFileLoader.Load(input.SourcePath, premultiplyAlpha: true);
		return new AsepriteContent(new AsepriteFile(input.File.Name, premultiplied.Palette, premultiplied.CanvasWidth, premultiplied.CanvasHeight,
			premultiplied.ColorDepth, premultiplied.Frames, premultiplied.Layers, premultiplied.Tags, premultiplied.Slices, premultiplied.Warnings), input.SourcePath);
	}
}
