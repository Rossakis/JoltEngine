using Microsoft.Xna.Framework.Content.Pipeline;

namespace Voltage.Pipeline.Fonts;

/// <summary>Reads a BMFont .fnt description in either its text or XML form.</summary>
[ContentImporter(".fnt", DisplayName = "BMFont - Voltage", DefaultProcessor = nameof(BitmapFontProcessor))]
public sealed class BitmapFontImporter : ContentImporter<BitmapFontContent>
{
	public override BitmapFontContent Import(string filename, ContentImporterContext context)
	{
		var font = BitmapFontContent.Load(filename);
		foreach (var page in font.Pages)
			context.AddDependency(page.File);
		return font;
	}
}
