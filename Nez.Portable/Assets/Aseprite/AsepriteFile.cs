using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Nez.Sprites;
using Nez.Textures;
using Nez.Utils.Extensions;

namespace Nez.Aseprite;

/// <summary>
/// Represents the contents loaded from an Aseprite file.
/// </summary>
public sealed class AsepriteFile
{
	/// <summary>
	/// The width, in pixels, defined for the canvas of the Aseprite image.
	/// </summary>
	/// <remarks>
	/// This is also the width of every frame.
	/// </remarks>
	public readonly int CanvasWidth;

	/// <summary>
	/// The height, in pixels, defined for the canvas of the Aseprite image.
	/// </summary>
	/// <remarks>
	/// This is also the height of every frame.
	/// </remarks>
	public readonly int CanvasHeight;

	/// <summary>
	/// The color depth mode used for the image in Aseprite which defines the total number of bits per pixel.
	/// </summary>
	public readonly AsepriteColorDepth ColorDepth;

	/// <summary>
	/// A collection of all frame elements in the Aseprite file.  Order of elements is from first-to-last.
	/// </summary>
	public readonly List<AsepriteFrame> Frames;

	/// <summary>
	/// A collection of all layer elements in the Aseprite file.  Order of elements is from bottom-to-top.
	/// </summary>
	public readonly List<AsepriteLayer> Layers;

	/// <summary>
	/// A collection of all tag elements from the Aseprite file.  Order of elements is as defined in the Aseprite UI
	/// from left-to-right.
	/// </summary>
	public readonly List<AsepriteTag> Tags;

	/// <summary>
	/// A collection of all slice elements from the Aseprite file.  Order of elements is in the order they were
	/// created in Aseprite.
	/// </summary>
	public readonly List<AsepriteSlice> Slices;

	/// <summary>
	/// A collection of any warnings issued when parsing the Aseprite file.  You can use this to see if there were
	/// any non-fatal errors that occurred while parsing the file.
	/// </summary>
	public readonly List<string> Warnings;

	/// <summary>
	/// The palette data from the Aseprite file containing the palette information and colors.
	/// </summary>
	public readonly AsepritePalette Palette;

	/// <summary>
	/// The custom user data that was set in the sprite properties in Aseprite.
	/// </summary>
	public AsepriteUserData UserData { get; }

	/// <summary>
	/// The name of this Aseprite file (without extension)
	/// </summary>
	public readonly string Name;

	internal AsepriteFile(string name, AsepritePalette palette, int width, int height, AsepriteColorDepth colorDepth,
		List<AsepriteFrame> frames, List<AsepriteLayer> layers, List<AsepriteTag> tags, List<AsepriteSlice> slices,
		List<string> warnings)
	{
		Name = name;
		CanvasWidth = width;
		CanvasHeight = height;
		ColorDepth = colorDepth;
		Frames = frames;
		Layers = layers;
		Tags = tags;
		Slices = slices;
		Warnings = warnings;
		Palette = palette;
	}

	/// <summary>
	/// Translates the data in this aseprite file to a sprite atlas that can be used in a sprite animator component.
	/// </summary>
	/// <param name="onlyVisibleLayers">
	/// Indicates whether only layers that are visible in the Aseprite file should be included when generating the 
	/// texture.
	/// </param>
	/// <param name="borderPadding">
	/// Indicates the amount of padding, in transparent pixels, to add to the edge of the generated texture.
	/// </param>
	/// <param name="spacing">
	/// Indicates the amount of padding, in transparent pixels, to add between each frame in the generated texture.
	/// </param>
	/// <param name="innerPadding">
	/// indicates the amount of padding, in transparent pixels, to add around the edges of each frame in the
	/// generated texture.
	/// </param>
	/// <param name="spriteOrigin">
	/// make the sprite origin something other than sourceRect.GetHalfSize()
	/// </param>
	/// <returns>
	/// A new instance of hte <see cref="SpriteAtlas"/> class initialized with the data generated from this Aseprite
	/// file.
	/// </returns>
	public SpriteAtlas ToSpriteAtlas(string layerName = null, bool onlyVisibleLayers = true, int borderPadding = 0, int spacing = 0, int innerPadding = 0, Vector2? spriteOrigin = null) 
	{
		var atlas = new SpriteAtlas
		{
			Names = new string[Frames.Count],
			Sprites = new Sprite[Frames.Count],
			SpriteAnimations = new SpriteAnimation[Tags.Count],
			AnimationNames = new string[Tags.Count]
		};

		var flattenedFrames = new Color[Frames.Count][];

		for (var i = 0; i < Frames.Count; i++) flattenedFrames[i] = Frames[i].FlattenFrame(onlyVisibleLayers, false, layerName);

		var sqrt = Math.Sqrt(Frames.Count);
		var columns = (int)Math.Ceiling(sqrt);
		var rows = (Frames.Count + columns - 1) / columns;

		var imageWidth = columns * CanvasWidth
		                 + borderPadding * 2
		                 + spacing * (columns - 1)
		                 + innerPadding * 2 * columns;

		var imageHeight = rows * CanvasHeight
		                  + borderPadding * 2
		                  + spacing * (rows - 1)
		                  + innerPadding * 2 * rows;

		var imagePixels = new Color[imageWidth * imageHeight];
		var regions = new Rectangle[Frames.Count];

		for (var i = 0; i < flattenedFrames.GetLength(0); i++)
		{
			var column = i % columns;
			var row = i / columns;
			var frame = flattenedFrames[i];

			var x = column * CanvasWidth
			        + borderPadding
			        + spacing * column
			        + innerPadding * (column + column + 1);

			var y = row * CanvasHeight
			        + borderPadding
			        + spacing * row
			        + innerPadding * (row + row + 1);

			for (var p = 0; p < frame.Length; p++)
			{
				var px = p % CanvasWidth + x;
				var py = p / CanvasWidth + y;

				var index = py * imageWidth + px;
				imagePixels[index] = frame[p];
			}

			regions[i] = new Rectangle(x, y, CanvasWidth, CanvasHeight);
		}

		var texture = new Texture2D(Core.GraphicsDevice, imageWidth, imageHeight);
		texture.SetData<Color>(imagePixels);

		for (var i = 0; i < Frames.Count; i++)
		{
			atlas.Sprites[i] = new Sprite(texture, regions[i], spriteOrigin ?? regions[i].GetHalfSize());
		}

		for (var tagNum = 0; tagNum < Tags.Count; tagNum++)
		{
			var tag = Tags[tagNum];
			var sprites = new Sprite[tag.To - tag.From + 1];
			var durations = new float[sprites.Length];

			for (int spriteIndex = 0, lookupIndex = tag.From;
			     spriteIndex < sprites.Length;
			     spriteIndex++, lookupIndex++)
			{
				sprites[spriteIndex] = atlas.Sprites[lookupIndex];
				durations[spriteIndex] = 1.0f / (Frames[lookupIndex].Duration / 1000.0f);
			}

			atlas.SpriteAnimations[tagNum] = new SpriteAnimation(sprites, durations);
			atlas.AnimationNames[tagNum] = tag.Name;
		}

		return atlas;
	}

	public SpriteAtlas ToSpriteAtlasFromLayers(bool onlyVisibleLayers = true, int borderPadding = 0, int spacing = 0, int innerPadding = 0, Vector2? spriteOrigin = null, params string[] layers)
	{
		var atlas = new SpriteAtlas
		{
			Names = new string[Frames.Count],
			Sprites = new Sprite[Frames.Count],
			SpriteAnimations = new SpriteAnimation[Tags.Count],
			AnimationNames = new string[Tags.Count]
		};

		var flattenedFrames = new Color[Frames.Count][];

		for (var i = 0; i < Frames.Count; i++)
		{
			flattenedFrames[i] = Frames[i].FlattenFrameOnLayers(onlyVisibleLayers, false, layers);
		}

		var sqrt = Math.Sqrt(Frames.Count);
		var columns = (int)Math.Ceiling(sqrt);
		var rows = (Frames.Count + columns - 1) / columns;

		var imageWidth = columns * CanvasWidth
						 + borderPadding * 2
						 + spacing * (columns - 1)
						 + innerPadding * 2 * columns;

		var imageHeight = rows * CanvasHeight
						  + borderPadding * 2
						  + spacing * (rows - 1)
						  + innerPadding * 2 * rows;

		var imagePixels = new Color[imageWidth * imageHeight];
		var regions = new Rectangle[Frames.Count];

		for (var i = 0; i < flattenedFrames.GetLength(0); i++)
		{
			var column = i % columns;
			var row = i / columns;
			var frame = flattenedFrames[i];

			var x = column * CanvasWidth
					+ borderPadding
					+ spacing * column
					+ innerPadding * (column + column + 1);

			var y = row * CanvasHeight
					+ borderPadding
					+ spacing * row
					+ innerPadding * (row + row + 1);

			for (var p = 0; p < frame.Length; p++)
			{
				var px = p % CanvasWidth + x;
				var py = p / CanvasWidth + y;

				var index = py * imageWidth + px;
				imagePixels[index] = frame[p];
			}

			regions[i] = new Rectangle(x, y, CanvasWidth, CanvasHeight);
		}

		var texture = new Texture2D(Core.GraphicsDevice, imageWidth, imageHeight);
		texture.SetData<Color>(imagePixels);

		for (var i = 0; i < Frames.Count; i++)
		{
			atlas.Sprites[i] = new Sprite(texture, regions[i], spriteOrigin ?? regions[i].GetHalfSize());
		}

		for (var tagNum = 0; tagNum < Tags.Count; tagNum++)
		{
			var tag = Tags[tagNum];
			var sprites = new Sprite[tag.To - tag.From + 1];
			var durations = new float[sprites.Length];

			for (int spriteIndex = 0, lookupIndex = tag.From;
				 spriteIndex < sprites.Length;
				 spriteIndex++, lookupIndex++)
			{
				sprites[spriteIndex] = atlas.Sprites[lookupIndex];
				durations[spriteIndex] = 1.0f / (Frames[lookupIndex].Duration / 1000.0f);
			}

			atlas.SpriteAnimations[tagNum] = new SpriteAnimation(sprites, durations);
			atlas.AnimationNames[tagNum] = tag.Name;
		}

		return atlas;
	}

	/// <summary>
	/// Translates the data in this aseprite file to a sprite atlas that can be used in a sprite animator component.
	/// </summary>
	/// <param name="spriteOrigin">
	/// make the sprite origin something other than sourceRect.GetHalfSize()
	/// </param>
	/// <returns>
	/// A new instance of hte <see cref="SpriteAtlas"/> class initialized with the data generated from this Aseprite
	/// file.
	/// </returns>
	public SpriteAtlas ToSpriteAtlasWithOrigin(Vector2 spriteOrigin, string layerName)
	{
		return ToSpriteAtlas(layerName, true, 0, 0, 0, spriteOrigin);
	}

	/// <summary>
	/// generate a Texture2D from a single aseprite frame.
	/// </summary>
	/// <param name="frameNumber">
	/// the number of the frame as show in Aseprite app.
	/// </param>
	/// <returns>
	/// A <see cref="Texture2D"/> instance with the flattened contents of the frame
	/// </returns>
	public Texture2D GetTextureFromFrameNumber(int frameNumber)
	{
		// frameNumber is base-one in the aseprite app,
		// but Frames array is base-zero, so we subtract 1
		var frame = Frames[frameNumber - 1];
		var pixels = frame.FlattenFrame(true, true);
		var texture = new Texture2D(Core.GraphicsDevice, frame.Width, frame.Height);
		texture.SetData<Color>(pixels);
		return texture;
	}
}