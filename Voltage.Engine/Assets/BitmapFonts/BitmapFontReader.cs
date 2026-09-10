using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;


namespace Voltage.BitmapFonts
{
	/// <summary>Reads compiled bitmap fonts: version 2 is what Voltage.Pipeline writes, anything else is the legacy Nez layout.</summary>
	public class BitmapFontReader : ContentTypeReader<BitmapFont>
	{
		public const byte Version = 2;

		protected override BitmapFont Read(ContentReader reader, BitmapFont existingInstance)
		{
			var first = reader.ReadByte();
			return first == Version ? ReadCompiled(reader) : ReadLegacy(reader, first != 0);
		}

		private static BitmapFont ReadCompiled(ContentReader reader)
		{
			var font = new BitmapFont
			{
				FamilyName = reader.ReadString(),
				FontSize = reader.ReadInt32(),
				Bold = reader.ReadBoolean(),
				Italic = reader.ReadBoolean(),
				Unicode = reader.ReadBoolean(),
				Smoothed = reader.ReadBoolean(),
				Packed = reader.ReadBoolean(),
				Charset = reader.ReadString(),
				StretchedHeight = reader.ReadInt32(),
				SuperSampling = reader.ReadInt32(),
				OutlineSize = reader.ReadInt32(),
				Padding = new Padding(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
				Spacing = new Point(reader.ReadInt32(), reader.ReadInt32()),
				LineHeight = reader.ReadInt32(),
				BaseHeight = reader.ReadInt32(),
				TextureSize = new Point(reader.ReadInt32(), reader.ReadInt32()),
				AlphaChannel = reader.ReadInt32(),
				RedChannel = reader.ReadInt32(),
				GreenChannel = reader.ReadInt32(),
				BlueChannel = reader.ReadInt32()
			};

			var pageCount = reader.ReadInt32();
			font.Pages = new Page[pageCount];
			font.Textures = new Texture2D[pageCount];
			for (var i = 0; i < pageCount; i++)
			{
				font.Pages[i] = new Page(reader.ReadInt32(), reader.ReadString());
				font.Textures[i] = reader.ReadExternalReference<Texture2D>();
			}

			var characterCount = reader.ReadInt32();
			var characters = new Dictionary<char, Character>(characterCount);
			for (var i = 0; i < characterCount; i++)
			{
				var character = new Character
				{
					Char = (char)reader.ReadInt32(),
					Bounds = new Rectangle(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
					Offset = new Point(reader.ReadInt32(), reader.ReadInt32()),
					XAdvance = reader.ReadInt32(),
					TexturePage = reader.ReadInt32(),
					Channel = reader.ReadInt32()
				};
				characters[character.Char] = character;
			}
			font.Characters = characters;

			var kerningCount = reader.ReadInt32();
			var kernings = new Dictionary<Kerning, int>(kerningCount);
			for (var i = 0; i < kerningCount; i++)
			{
				var kerning = new Kerning((char)reader.ReadInt32(), (char)reader.ReadInt32(), reader.ReadInt32());
				kernings[kerning] = kerning.Amount;
			}
			font.Kernings = kernings;

			Finish(font);
			return font;
		}

		private static BitmapFont ReadLegacy(ContentReader reader, bool hasEmbeddedTextures)
		{
			Texture2D[] textures;
			if (hasEmbeddedTextures)
			{
				var totalTextures = reader.ReadInt32();
				textures = new Texture2D[totalTextures];
				for (var i = 0; i < totalTextures; i++)
					textures[i] = reader.ReadObject<Texture2D>();
			}
			else
			{
				var totalTextureNames = reader.ReadInt32();
				textures = new Texture2D[totalTextureNames];
				for (var i = 0; i < totalTextureNames; i++)
				{
					var textureName = reader.ReadString();
					reader.ReadSingle();
					reader.ReadSingle();
					textures[i] = reader.ContentManager.Load<Texture2D>(textureName);
				}
			}

			var lineHeight = reader.ReadInt32();
			var padTop = reader.ReadInt32();
			var padLeft = reader.ReadInt32();
			var padBottom = reader.ReadInt32();
			var padRight = reader.ReadInt32();
			reader.ReadInt32();

			var regionCount = reader.ReadInt32();
			var characters = new Dictionary<char, Character>();
			for (var r = 0; r < regionCount; r++)
			{
				var character = new Character();
				character.Char = (char)reader.ReadInt32();
				character.TexturePage = reader.ReadInt32();
				character.Bounds.X = reader.ReadInt32();
				character.Bounds.Y = reader.ReadInt32();
				character.Bounds.Width = reader.ReadInt32();
				character.Bounds.Height = reader.ReadInt32();
				character.Offset.X = reader.ReadInt32();
				character.Offset.Y = reader.ReadInt32();
				character.XAdvance = reader.ReadInt32();
				characters[character.Char] = character;
			}

			var font = new BitmapFont
			{
				Kernings = new Dictionary<Kerning, int>(),
				Textures = textures,
				LineHeight = lineHeight,
				Padding = new Padding(padLeft, padTop, padRight, padBottom),
				Characters = characters
			};
			Finish(font);
			return font;
		}

		/// <summary>What <see cref="BitmapFont.Initialize"/> does after the textures exist.</summary>
		private static void Finish(BitmapFont font)
		{
			font.DefaultCharacter = font.Characters.TryGetValue(' ', out var space) ? space : font['a'];
			font._spaceWidth = font.DefaultCharacter.Bounds.Width + font.DefaultCharacter.XAdvance;
		}
	}
}
