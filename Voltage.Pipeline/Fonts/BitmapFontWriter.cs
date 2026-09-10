using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Serialization.Compiler;

namespace Voltage.Pipeline.Fonts;

/// <summary>Version 2 of the layout <c>Voltage.BitmapFonts.BitmapFontReader</c> reads.</summary>
[ContentTypeWriter]
public sealed class BitmapFontWriter : ContentTypeWriter<BitmapFontContent>
{
	private const byte Version = 2;

	protected override void Write(ContentWriter output, BitmapFontContent font)
	{
		output.Write(Version);
		output.Write(font.FamilyName ?? "");
		output.Write(font.FontSize);
		output.Write(font.Bold);
		output.Write(font.Italic);
		output.Write(font.Unicode);
		output.Write(font.Smoothed);
		output.Write(font.Packed);
		output.Write(font.Charset ?? "");
		output.Write(font.StretchedHeight);
		output.Write(font.SuperSampling);
		output.Write(font.OutlineSize);
		output.Write(font.PaddingLeft);
		output.Write(font.PaddingTop);
		output.Write(font.PaddingRight);
		output.Write(font.PaddingBottom);
		output.Write(font.SpacingX);
		output.Write(font.SpacingY);
		output.Write(font.LineHeight);
		output.Write(font.BaseHeight);
		output.Write(font.TextureWidth);
		output.Write(font.TextureHeight);
		output.Write(font.AlphaChannel);
		output.Write(font.RedChannel);
		output.Write(font.GreenChannel);
		output.Write(font.BlueChannel);

		output.Write(font.Pages.Count);
		foreach (var page in font.Pages)
		{
			output.Write(page.Id);
			output.Write(System.IO.Path.GetFileName(page.File) ?? "");
			output.WriteExternalReference(page.Texture);
		}

		output.Write(font.Glyphs.Count);
		foreach (var glyph in font.Glyphs)
		{
			output.Write(glyph.Id);
			output.Write(glyph.X);
			output.Write(glyph.Y);
			output.Write(glyph.Width);
			output.Write(glyph.Height);
			output.Write(glyph.XOffset);
			output.Write(glyph.YOffset);
			output.Write(glyph.XAdvance);
			output.Write(glyph.Page);
			output.Write(glyph.Channel);
		}

		output.Write(font.Kernings.Count);
		foreach (var kerning in font.Kernings)
		{
			output.Write(kerning.First);
			output.Write(kerning.Second);
			output.Write(kerning.Amount);
		}
	}

	public override string GetRuntimeReader(TargetPlatform targetPlatform) => "Voltage.BitmapFonts.BitmapFontReader, Voltage";

	public override string GetRuntimeType(TargetPlatform targetPlatform) => "Voltage.BitmapFonts.BitmapFont, Voltage";
}
