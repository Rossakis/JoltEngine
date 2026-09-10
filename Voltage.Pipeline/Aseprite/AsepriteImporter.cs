using System.IO;
using Microsoft.Xna.Framework.Content.Pipeline;
using Voltage.Aseprite;

namespace Voltage.Pipeline.Aseprite;

/// <summary>Parses .aseprite and .ase files with the engine's own loader.</summary>
[ContentImporter(".aseprite", ".ase", DisplayName = "Aseprite - Voltage", DefaultProcessor = nameof(AsepriteProcessor))]
public sealed class AsepriteImporter : ContentImporter<AsepriteContent>
{
	public override AsepriteContent Import(string filename, ContentImporterContext context)
	{
		var file = AsepriteFileLoader.Load(filename);
		foreach (var warning in file.Warnings)
			context.Logger.LogWarning(null, new ContentIdentity(filename), warning);
		return new AsepriteContent(Rename(file, Path.GetFileNameWithoutExtension(filename)), filename);
	}

	/// <summary>The loader names the file and its frames by the full path; a compiled asset carries the bare name.</summary>
	private static AsepriteFile Rename(AsepriteFile file, string name)
	{
		var frames = new System.Collections.Generic.List<AsepriteFrame>(file.Frames.Count);
		for (var i = 0; i < file.Frames.Count; i++)
		{
			var frame = file.Frames[i];
			frames.Add(new AsepriteFrame($"{name}_{i}", frame.Duration, frame.Cels, frame.Width, frame.Height));
		}
		return new AsepriteFile(name, file.Palette, file.CanvasWidth, file.CanvasHeight, file.ColorDepth, frames, file.Layers, file.Tags, file.Slices, file.Warnings);
	}
}
