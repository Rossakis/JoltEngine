using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Nez;
using Nez.Sprites;
using Nez.Textures;

namespace Nez.Utils;

public class AnimationUtils
{
    /// <summary>
    /// Example for naming animations: If aseprite animation has tag "Idle", but you want it to be called by a different name in the animator (e.g. "Character-Idle")
    /// then the method call would be LoadAsepriteAnimation(animator, asepriteFile, "Idle", "Character-Idle").
    /// <para> If <c>callableAnimationName</c> is not changed, then the <c>animationTagName</c>  will be given to the animation instead (useful if if animation tag and animation name have always the same name)</para>
    /// </summary>
    /// <param name="animationTagName">The name of the tag containing the desired animation in the aseprite file.</param>
    /// <param name="callableAnimationName">The name we want to give the animation, which we will need to call when using Animator.Play("animation")</param>
    /// <param name="layerName">If left as null, then animator will select ALL layers for the animation.</param>
    public static void LoadAsepriteAnimation(Entity entity, string asepriteFilePath, string animationTagName, string callableAnimationName = null, string layerName = null)
    {
        SpriteAtlas sprite = entity.Scene.Content.LoadAsepriteFile(asepriteFilePath).ToSpriteAtlas(layerName);

        if (callableAnimationName == null) // animation name not assigned
            entity.GetComponent<SpriteAnimator>().AddAnimation(animationTagName, sprite.GetAnimation(animationTagName));
        else
            entity.GetComponent<SpriteAnimator>().AddAnimation(callableAnimationName, sprite.GetAnimation(animationTagName));
    }

    /// <summary>
    /// Create animation only based on its specific layers 
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="asepriteFilePath"></param>
    /// <param name="animationTagName"></param>
    /// <param name="callableAnimationName"></param>
    /// <param name="layers"></param>
    public static void LoadAsepriteAnimationWithLayers(Entity entity, string asepriteFilePath, string animationTagName, string callableAnimationName = null, params string[] layers)
    {
        SpriteAtlas sprite = entity.Scene.Content.LoadAsepriteFile(asepriteFilePath).ToSpriteAtlasFromLayers(true, 0, 0 ,0 , null, layers);

        if (callableAnimationName == null) 
            entity.GetComponent<SpriteAnimator>().AddAnimation(animationTagName, sprite.GetAnimation(animationTagName));
        else
            entity.GetComponent<SpriteAnimator>().AddAnimation(callableAnimationName, sprite.GetAnimation(animationTagName));
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
    public static Sprite LoadAsepriteFrame(Entity entity, string asepriteFilePath, int frameNumber, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false, params string[] layerNames)
    {
        // Handle case where entity might not be in a scene yet
        var contentManager = entity?.Scene?.Content ?? Core.Content;
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
    public static Sprite LoadAsepriteFrameFromLayer(Entity entity, string asepriteFilePath, int frameNumber, string layerName, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false)
    {
        // Handle case where entity might not be in a scene yet
        var contentManager = entity?.Scene?.Content ?? Core.Content;
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
}