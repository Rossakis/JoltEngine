using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Nez;
using Nez.Sprites;
using Nez.Textures;
using Nez.Tiled;

namespace Nez.Utils;

public class AsepriteUtils
{
    /// <summary>
    /// Example for naming animations: If aseprite animation has tag "Idle", but you want it to be called by a different name in the animator (e.g. "Character-Idle")
    /// then the method call would be LoadAsepriteAnimation(animator, asepriteFile, "Idle", "Character-Idle").
    /// <para> If <c>callableAnimationName</c> is not changed, then the <c>animationTagName</c>  will be given to the animation instead (useful if if animation tag and animation name have always the same name)</para>
    /// </summary>
    /// <param name="animationTagName">The name of the tag containing the desired animation in the aseprite file.</param>
    /// <param name="callableAnimationName">The name we want to give the animation, which we will need to call when using Animator.Play("animation")</param>
    /// <param name="layerName">If left as null, then animator will select ALL layers for the animation.</param>
    public static SpriteAnimation LoadAsepriteAnimation(Entity entity, string asepriteFilePath, string animationTagName, string callableAnimationName = null, string layerName = null)
    {
        SpriteAtlas sprite = entity.Scene.Content.LoadAsepriteFile(asepriteFilePath).ToSpriteAtlas(layerName);

        if (callableAnimationName == null) // animation name not assigned
            entity.GetComponent<SpriteAnimator>().AddAnimation(animationTagName, sprite.GetAnimation(animationTagName));
        else
            entity.GetComponent<SpriteAnimator>().AddAnimation(callableAnimationName, sprite.GetAnimation(animationTagName));

        return sprite.GetAnimation(animationTagName);
    }

    public static SpriteAnimation LoadAsepriteAnimation(SpriteAnimator animator, string asepriteFilePath, string animationTagName, string callableAnimationName = null, string layerName = null)
    {
	    SpriteAtlas sprite = Core.Scene.Content.LoadAsepriteFile(asepriteFilePath).ToSpriteAtlas(layerName);

	    if (callableAnimationName == null) // animation name not assigned
		    animator.AddAnimation(animationTagName, sprite.GetAnimation(animationTagName));
	    else
		    animator.AddAnimation(callableAnimationName, sprite.GetAnimation(animationTagName));

	    return sprite.GetAnimation(animationTagName);
	}

	/// <summary>
	/// Create animation only based on its specific layers 
	/// </summary>
	/// <param name="entity"></param>
	/// <param name="asepriteFilePath"></param>
	/// <param name="animationTagName"></param>
	/// <param name="callableAnimationName"></param>
	/// <param name="layers"></param>
	public static SpriteAnimation LoadAsepriteAnimationWithLayers(Entity entity, string asepriteFilePath, string animationTagName, string callableAnimationName = null, params string[] layers)
    {
        SpriteAtlas sprite = entity.Scene.Content.LoadAsepriteFile(asepriteFilePath).ToSpriteAtlasFromLayers(true, 0, 0 ,0 , null, layers);

        if (callableAnimationName == null) 
            entity.GetComponent<SpriteAnimator>().AddAnimation(animationTagName, sprite.GetAnimation(animationTagName));
        else
            entity.GetComponent<SpriteAnimator>().AddAnimation(callableAnimationName, sprite.GetAnimation(animationTagName));

        return sprite.GetAnimation(animationTagName);
	}

	public static SpriteAnimation LoadAsepriteAnimationWithLayers(SpriteAnimator animator, string asepriteFilePath, string animationTagName, string callableAnimationName = null, params string[] layers)
	{
		SpriteAtlas sprite = Core.Scene.Content.LoadAsepriteFile(asepriteFilePath).ToSpriteAtlasFromLayers(true, 0, 0, 0, null, layers);

		if (callableAnimationName == null)
			animator.AddAnimation(animationTagName, sprite.GetAnimation(animationTagName));
		else
			animator.AddAnimation(callableAnimationName, sprite.GetAnimation(animationTagName));

		return sprite.GetAnimation(animationTagName);
	}

	/// <summary>
	/// Loads a specific frame from an Aseprite file as a Sprite, with optional layer filtering.
	/// </summary>
	/// <param name="entity">The entity to load the frame for (used to access the content manager)</param>
	/// <param name="asepriteFilePath">The path to the Aseprite file</param>
	/// <param name="frameNumber">The frame number to load (0-based index)</param>
	/// <param name="onlyVisibleLayers">Whether to only include visible layers when flattening the frame</param>
	/// <param name="includeBackgroundLayer">Whether to include the background layer when flattening the frame</param>
	/// <param name="layerNames">Optional array of specific layer names to include. If null, all layers (subject to other filters) will be included</param>
	/// <returns>A Sprite containing the flattened frame data as a Texture2D</returns>
	public static Sprite LoadAsepriteFrame(string asepriteFilePath, int frameNumber, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false, params string[] layerNames)
    {
        // Handle case where entity might not be in a scene yet
        var contentManager = Core.Scene?.Content ?? Core.Content;
        if (contentManager == null)
        {
            throw new InvalidOperationException($"Cannot load Aseprite file '{asepriteFilePath}' - no content manager available. Entity must be added to a scene first.");
        }
        
        var asepriteFile = contentManager.LoadAsepriteFile(asepriteFilePath);
        
        // Validate frame number
        if (frameNumber < 0 || frameNumber >= asepriteFile.Frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(frameNumber), 
                $"Frame number {frameNumber} is out of range. File has {asepriteFile.Frames.Count} frames.");
        }
        
        var frame = asepriteFile.Frames[frameNumber];
        Color[] pixels;
        
        // Choose the appropriate flattening method based on whether specific layers are requested
        if (layerNames != null && layerNames.Length > 0)
        {
            pixels = frame.FlattenFrameOnLayers(onlyVisibleLayers, includeBackgroundLayer, layerNames);
        }
        else
        {
            pixels = frame.FlattenFrame(onlyVisibleLayers, includeBackgroundLayer);
        }
        
        // Create texture from the flattened pixel data
        var texture = new Texture2D(Core.GraphicsDevice, frame.Width, frame.Height);
        texture.SetData<Color>(pixels);
        texture.Name = $"{asepriteFilePath}_frame_{frameNumber}";
        
        return new Sprite(texture);
    }

    /// <summary>
    /// Loads a specific frame from an Aseprite file as a Sprite, filtering by a single layer name.
    /// </summary>
    /// <param name="entity">The entity to load the frame for (used to access the content manager)</param>
    /// <param name="asepriteFilePath">The path to the Aseprite file</param>
    /// <param name="frameNumber">The frame number to load (0-based index)</param>
    /// <param name="layerName">The specific layer name to include when flattening the frame</param>
    /// <param name="onlyVisibleLayers">Whether to only include visible layers when flattening the frame</param>
    /// <param name="includeBackgroundLayer">Whether to include the background layer when flattening the frame</param>
    /// <returns>A Sprite containing the flattened frame data as a Texture2D</returns>
    public static Sprite LoadAsepriteFrameFromLayer(string asepriteFilePath, int frameNumber, string layerName, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false)
    {
        // Handle case where entity might not be in a scene yet
        var contentManager = Core.Scene?.Content ?? Core.Content;
        if (contentManager == null)
        {
            throw new InvalidOperationException($"Cannot load Aseprite file '{asepriteFilePath}' - no content manager available. Entity must be added to a scene first.");
        }
        
        var asepriteFile = contentManager.LoadAsepriteFile(asepriteFilePath);
        
        // Validate frame number
        if (frameNumber < 0 || frameNumber >= asepriteFile.Frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(frameNumber), 
                $"Frame number {frameNumber} is out of range. File has {asepriteFile.Frames.Count} frames.");
        }
        
        var frame = asepriteFile.Frames[frameNumber];
        var pixels = frame.FlattenFrame(onlyVisibleLayers, includeBackgroundLayer, layerName);
        
        // Create texture from the flattened pixel data
        var texture = new Texture2D(Core.GraphicsDevice, frame.Width, frame.Height);
        texture.SetData<Color>(pixels);
        texture.Name = $"{asepriteFilePath}_frame_{frameNumber}_layer_{layerName}";
        
        return new Sprite(texture);
    }

    /// <summary>
    /// Calculates the appropriate render layer based on the Aseprite layer's position in the layer hierarchy.
    /// The further back a layer was in Aseprite, the closer it will be to RenderOrder.BehindAll.
    /// </summary>
    /// <param name="layerIndex">The index of the layer in the Aseprite file (0 = bottom layer)</param>
    /// <param name="totalLayers">Total number of layers in the Aseprite file</param>
    /// <returns>The calculated render layer value</returns>
    public static int CalculateRenderLayerFromAsepriteIndex(int layerIndex, int totalLayers, int minRenderLayer, int maxRenderLayer)
    {
	    int layerSpan = maxRenderLayer - minRenderLayer;

	    if (totalLayers <= 1)
	    {
		    // If there's only one layer, put it in the middle of the range
		    return minRenderLayer + (layerSpan / 2);
	    }

	    // Map the Aseprite layer index to our render layer range
	    // layerIndex 0 (bottom/background) -> minSpriteLayer
	    // layerIndex (totalLayers-1) (top/foreground) -> maxSpriteLayer
	    float normalizedPosition = (float)layerIndex / (totalLayers - 1);
	    int renderLayer = minRenderLayer + (int)(normalizedPosition * layerSpan);

	    return renderLayer;
    }
}