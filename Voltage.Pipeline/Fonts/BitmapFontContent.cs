using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;

namespace Voltage.Pipeline.Fonts;

/// <summary>A BMFont description (text or XML) with its page images; mirrors the fields the engine's BitmapFont keeps.</summary>
public sealed class BitmapFontContent
{
	public sealed class Glyph
	{
		public int Id, X, Y, Width, Height, XOffset, YOffset, XAdvance, Page, Channel;
	}

	public sealed class Kern
	{
		public int First, Second, Amount;
	}

	public sealed class PageInfo
	{
		public int Id;
		public string File;
		public ExternalReference<TextureContent> Texture;
	}

	public string FamilyName = "";
	public int FontSize, StretchedHeight, SuperSampling, OutlineSize;
	public bool Bold, Italic, Unicode, Smoothed, Packed;
	public string Charset = "";
	public int PaddingLeft, PaddingTop, PaddingRight, PaddingBottom;
	public int SpacingX, SpacingY;
	public int LineHeight, BaseHeight, TextureWidth, TextureHeight;
	public int AlphaChannel, RedChannel, GreenChannel, BlueChannel;
	public readonly List<PageInfo> Pages = new();
	public readonly List<Glyph> Glyphs = new();
	public readonly List<Kern> Kernings = new();

	public static BitmapFontContent Load(string path)
	{
		var text = File.ReadAllText(path);
		var trimmed = text.TrimStart();
		var content = trimmed.StartsWith("<", StringComparison.Ordinal) ? ParseXml(text) : ParseText(text);
		var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
		foreach (var page in content.Pages)
			page.File = Path.Combine(directory, page.File);
		return content;
	}

	private static BitmapFontContent ParseText(string text)
	{
		var font = new BitmapFontContent();
		foreach (var rawLine in text.Split('\n'))
		{
			var line = rawLine.Trim();
			var space = line.IndexOf(' ');
			if (space <= 0)
				continue;
			var keyword = line.Substring(0, space);
			var values = ParsePairs(line.Substring(space + 1));
			switch (keyword)
			{
				case "info":
					font.FamilyName = Str(values, "face");
					font.FontSize = Int(values, "size");
					font.Bold = Int(values, "bold") != 0;
					font.Italic = Int(values, "italic") != 0;
					font.Charset = Str(values, "charset");
					font.Unicode = Int(values, "unicode") != 0;
					font.StretchedHeight = Int(values, "stretchH");
					font.Smoothed = Int(values, "smooth") != 0;
					font.SuperSampling = Int(values, "aa");
					font.OutlineSize = Int(values, "outline");
					var padding = Ints(values, "padding", 4);
					font.PaddingTop = padding[0];
					font.PaddingRight = padding[1];
					font.PaddingBottom = padding[2];
					font.PaddingLeft = padding[3];
					var spacing = Ints(values, "spacing", 2);
					font.SpacingX = spacing[0];
					font.SpacingY = spacing[1];
					break;
				case "common":
					font.LineHeight = Int(values, "lineHeight");
					font.BaseHeight = Int(values, "base");
					font.TextureWidth = Int(values, "scaleW");
					font.TextureHeight = Int(values, "scaleH");
					font.Packed = Int(values, "packed") != 0;
					font.AlphaChannel = Int(values, "alphaChnl");
					font.RedChannel = Int(values, "redChnl");
					font.GreenChannel = Int(values, "greenChnl");
					font.BlueChannel = Int(values, "blueChnl");
					break;
				case "page":
					font.Pages.Add(new PageInfo { Id = Int(values, "id"), File = Str(values, "file") });
					break;
				case "char":
					font.Glyphs.Add(new Glyph
					{
						Id = Int(values, "id"), X = Int(values, "x"), Y = Int(values, "y"), Width = Int(values, "width"), Height = Int(values, "height"),
						XOffset = Int(values, "xoffset"), YOffset = Int(values, "yoffset"), XAdvance = Int(values, "xadvance"), Page = Int(values, "page"), Channel = Int(values, "chnl")
					});
					break;
				case "kerning":
					font.Kernings.Add(new Kern { First = Int(values, "first"), Second = Int(values, "second"), Amount = Int(values, "amount") });
					break;
			}
		}
		font.Pages.Sort((a, b) => a.Id.CompareTo(b.Id));
		return font;
	}

	private static BitmapFontContent ParseXml(string text)
	{
		var font = new BitmapFontContent();
		var document = new XmlDocument();
		document.LoadXml(text);
		var root = document.DocumentElement ?? throw new InvalidContentException("empty BMFont XML");

		var info = root.SelectSingleNode("info");
		if (info != null)
		{
			font.FamilyName = Attr(info, "face");
			font.FontSize = AttrInt(info, "size");
			font.Bold = AttrInt(info, "bold") != 0;
			font.Italic = AttrInt(info, "italic") != 0;
			font.Charset = Attr(info, "charset");
			font.Unicode = AttrInt(info, "unicode") != 0;
			font.StretchedHeight = AttrInt(info, "stretchH");
			font.Smoothed = AttrInt(info, "smooth") != 0;
			font.SuperSampling = AttrInt(info, "aa");
			font.OutlineSize = AttrInt(info, "outline");
			var padding = SplitInts(Attr(info, "padding"), 4);
			font.PaddingTop = padding[0];
			font.PaddingRight = padding[1];
			font.PaddingBottom = padding[2];
			font.PaddingLeft = padding[3];
			var spacing = SplitInts(Attr(info, "spacing"), 2);
			font.SpacingX = spacing[0];
			font.SpacingY = spacing[1];
		}

		var common = root.SelectSingleNode("common");
		if (common != null)
		{
			font.LineHeight = AttrInt(common, "lineHeight");
			font.BaseHeight = AttrInt(common, "base");
			font.TextureWidth = AttrInt(common, "scaleW");
			font.TextureHeight = AttrInt(common, "scaleH");
			font.Packed = AttrInt(common, "packed") != 0;
			font.AlphaChannel = AttrInt(common, "alphaChnl");
			font.RedChannel = AttrInt(common, "redChnl");
			font.GreenChannel = AttrInt(common, "greenChnl");
			font.BlueChannel = AttrInt(common, "blueChnl");
		}

		foreach (XmlNode node in root.SelectNodes("pages/page") ?? throw new InvalidContentException("BMFont XML has no pages"))
			font.Pages.Add(new PageInfo { Id = AttrInt(node, "id"), File = Attr(node, "file") });
		foreach (XmlNode node in root.SelectNodes("chars/char"))
			font.Glyphs.Add(new Glyph
			{
				Id = AttrInt(node, "id"), X = AttrInt(node, "x"), Y = AttrInt(node, "y"), Width = AttrInt(node, "width"), Height = AttrInt(node, "height"),
				XOffset = AttrInt(node, "xoffset"), YOffset = AttrInt(node, "yoffset"), XAdvance = AttrInt(node, "xadvance"), Page = AttrInt(node, "page"), Channel = AttrInt(node, "chnl")
			});
		foreach (XmlNode node in root.SelectNodes("kernings/kerning"))
			font.Kernings.Add(new Kern { First = AttrInt(node, "first"), Second = AttrInt(node, "second"), Amount = AttrInt(node, "amount") });
		font.Pages.Sort((a, b) => a.Id.CompareTo(b.Id));
		return font;
	}

	/// <summary>Splits BMFont key=value pairs, honouring quoted values with spaces.</summary>
	private static Dictionary<string, string> ParsePairs(string text)
	{
		var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
		var i = 0;
		while (i < text.Length)
		{
			while (i < text.Length && text[i] == ' ')
				i++;
			var eq = text.IndexOf('=', i);
			if (eq < 0)
				break;
			var key = text.Substring(i, eq - i).Trim();
			i = eq + 1;
			string value;
			if (i < text.Length && text[i] == '"')
			{
				var close = text.IndexOf('"', i + 1);
				if (close < 0)
					close = text.Length;
				value = text.Substring(i + 1, close - i - 1);
				i = close + 1;
			}
			else
			{
				var end = text.IndexOf(' ', i);
				if (end < 0)
					end = text.Length;
				value = text.Substring(i, end - i);
				i = end;
			}
			pairs[key] = value;
		}
		return pairs;
	}

	private static string Str(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var v) ? v : "";

	private static int Int(Dictionary<string, string> values, string key) =>
		values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

	private static int[] Ints(Dictionary<string, string> values, string key, int count) => SplitInts(Str(values, key), count);

	private static int[] SplitInts(string text, int count)
	{
		var result = new int[count];
		var parts = (text ?? "").Split(',');
		for (var i = 0; i < count && i < parts.Length; i++)
			int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]);
		return result;
	}

	private static string Attr(XmlNode node, string name) => node.Attributes?[name]?.Value ?? "";

	private static int AttrInt(XmlNode node, string name) =>
		int.TryParse(Attr(node, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
