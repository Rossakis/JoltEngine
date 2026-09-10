using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Utils.Extensions;
using Voltage.Sprites;
using Voltage.Textures;

namespace Voltage.Aseprite;

/// <summary>Texture and atlas creation; the data half of the class is shared with the content pipeline.</summary>
public sealed partial class AsepriteFile
{
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
		AsepriteFrame frame;

		// frameNumber is base-one in the aseprite app,
		// but Frames array is base-zero, so we subtract 1
		frame = Frames[frameNumber - 1];

		var pixels = frame.FlattenFrame(true, true);
		var texture = new Texture2D(Core.GraphicsDevice, frame.Width, frame.Height);
		texture.SetData<Color>(pixels);
		return texture;
	}

	public Texture2D GetTextureFromLayer(string layer, int frameNumber = 1, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false)
	{
		int frameIndex = frameNumber - 1;
		// Validate frame number
		if (frameNumber < 0 || frameIndex >= Frames.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(frameNumber),
				$"Frame number {frameNumber} is out of range. File has {Frames.Count} frames.");
		}
	
		var frame = Frames[frameIndex];
		var pixels = frame.FlattenFrame(onlyVisibleLayers, includeBackgroundLayer, layer);
	
		// Create texture from the flattened pixel data
		var texture = new Texture2D(Core.GraphicsDevice, frame.Width, frame.Height);
		texture.SetData<Color>(pixels);
	
		return texture;
	}

	/// <summary>
	/// Generates a <see cref="Texture2D"/> from a specific frame, flattening only the specified layers.
	/// </summary>
	/// <param name="frameNumber">The frame number to use (1-based, matching the Aseprite UI).</param>
	/// <param name="onlyVisibleLayers">Whether to skip layers that are hidden in Aseprite.</param>
	/// <param name="includeBackgroundLayer">Whether to include the background layer.</param>
	/// <param name="layers">One or more layer names to include when flattening. If empty, all layers are used.</param>
	public Texture2D GetTextureFromLayers(int frameNumber = 1, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false, params string[] layers)
	{
		int frameIndex = frameNumber - 1;

		if (frameIndex < 0 || frameIndex >= Frames.Count)
			throw new ArgumentOutOfRangeException(nameof(frameNumber),
				$"Frame number {frameNumber} is out of range. File has {Frames.Count} frames.");

		var frame = Frames[frameIndex];

		Color[] pixels;
		if (layers == null || layers.Length == 0)
			pixels = frame.FlattenFrame(onlyVisibleLayers, includeBackgroundLayer);
		else if (layers.Length == 1)
			pixels = frame.FlattenFrame(onlyVisibleLayers, includeBackgroundLayer, layers[0]);
		else
			pixels = frame.FlattenFrameOnLayers(onlyVisibleLayers, includeBackgroundLayer, layers);

		var texture = new Texture2D(Core.GraphicsDevice, frame.Width, frame.Height);
		texture.SetData<Color>(pixels);
		return texture;
	}
}
