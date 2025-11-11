using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Nez.Materials;
using Nez.Textures;
using Nez.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Nez.DeferredLighting;

namespace Nez.Sprites;

public class AnimationEvent
{
    public int StartFrame;
    public string Name;
    public Action Callback;
    public string AnimationName;
    public string EventType;

    public AnimationEvent() { }

    public AnimationEvent(int frame, string name, Action callback, string animationName = null)
    {
        StartFrame = frame;
        Name = name;
        Callback = callback;
        AnimationName = animationName;
        EventType = "DefaultEvent";
    }
}

public class LongAnimationEvent : AnimationEvent
{
	public int EndFrame;

	public LongAnimationEvent() { }

	public LongAnimationEvent(int frameStart, int frameEnd, string name, Action callback, string animationName = null)
	{
		StartFrame = frameStart;
		EndFrame = frameEnd;
		Name = name;
		Callback = callback;
		AnimationName = animationName;
		EventType = "LongEvent";
	}
}

/// <summary>
/// SpriteAnimator handles the display and animation of a sprite
/// </summary>
public class SpriteAnimator : SpriteRenderer, IUpdatable
{
	/// <summary>
	/// Serializable data for SpriteAnimator component.
	/// </summary>
	public class SpriteAnimatorComponentData : ComponentData
	{
		public string CurrentAnimationName = "";
		public bool IsPlaying = false;
		public float Speed = 1.0f;
		public SpriteAnimator.LoopMode CurrentLoopMode = SpriteAnimator.LoopMode.Loop;
		public int CurrentFrame = 0;
		public float ElapsedTime = 0f;

		// SpriteRenderer properties - store color as individual RGBA components for proper serialization
		public string TextureFilePath = "";
		public byte ColorR = 255;
		public byte ColorG = 255;
		public byte ColorB = 255;
		public byte ColorA = 255;
		public Vector2 LocalOffset = Vector2.Zero;
		public Vector2 Origin = Vector2.Zero;
		public float LayerDepth = 0f;
		public int RenderLayer = 0;
		public SpriteEffects SpriteEffects = SpriteEffects.None;
		public List<string> LoadedLayers = new();
		public string LoadedTag = "";

		// Animation events
		public List<AnimationEvent> AnimationEvents = new();

		// Helper property to get/set Color easily (not serialized)
		public Color Color
		{
			get => new Color(ColorR, ColorG, ColorB, ColorA);
			set
			{
				ColorR = value.R;
				ColorG = value.G;
				ColorB = value.B;
				ColorA = value.A;
			}
		}

		public SpriteAnimatorComponentData()
		{
			// Initialize with default values
			CurrentAnimationName = "";
			IsPlaying = false;
			Speed = 1.0f;
			CurrentLoopMode = SpriteAnimator.LoopMode.Loop;
			CurrentFrame = 0;
			ElapsedTime = 0f;

			// SpriteRenderer defaults
			TextureFilePath = "";
			ColorR = 255;
			ColorG = 255;
			ColorB = 255;
			ColorA = 255;
			LocalOffset = Vector2.Zero;
			Origin = Vector2.Zero;
			LayerDepth = 0f;
			RenderLayer = 0;
			Enabled = true;
			SpriteEffects = SpriteEffects.None;

			AnimationEvents = new List<AnimationEvent>();
		}

		public SpriteAnimatorComponentData(SpriteAnimator animator)
		{
			// Capture current animation state
			CurrentAnimationName = animator.CurrentAnimationName ?? "";
			IsPlaying = animator.IsRunning;
			Speed = animator.Speed;
			CurrentLoopMode = animator.CurrentLoopMode;
			CurrentFrame = animator.CurrentFrame;
			ElapsedTime = animator.CurrentElapsedTime;

			// Capture SpriteRenderer properties using the Color helper property
			TextureFilePath = animator.TextureFilePath ?? "";
			Color = animator.Color;  // This uses the helper property to set RGBA components
			LocalOffset = animator.LocalOffset;
			Origin = animator.Origin;
			LayerDepth = animator.LayerDepth;
			RenderLayer = animator.RenderLayer;
			Enabled = animator.Enabled;
			SpriteEffects = animator.SpriteEffects;

			// Copy animation events
			AnimationEvents = animator.AnimationEvents != null
				? new List<AnimationEvent>(animator.AnimationEvents)
				: new List<AnimationEvent>();

			LoadedLayers = animator.LoadedLayers != null ? new List<string>(animator.LoadedLayers) : new List<string>();
			LoadedTag = animator.LoadedTag ?? "";
		}
	}

	private SpriteAnimatorComponentData _animatorData = new SpriteAnimatorComponentData();

	/// <summary>
	/// List of animation events for this animator.
	/// </summary>
	public List<AnimationEvent> AnimationEvents { get; set; } = new();
	private Dictionary<(string animationName, string eventName), List<Action>> _animationEventSubscribers = new();
	/// <summary>
	/// The file path to the Aseprite file used for loading animations.
	/// </summary>
	public string TextureFilePath { get; set; } = "";

	public List<string> LoadedLayers { get; set; } = new();
	public string LoadedTag { get; set; } = "";

	public override ComponentData Data
	{
		get
		{
			// Always ensure _animatorData exists
			if (_animatorData == null)
				_animatorData = new SpriteAnimatorComponentData();

			// Update current animation state
			_animatorData.CurrentAnimationName = CurrentAnimationName ?? "";
			_animatorData.IsPlaying = IsRunning;
			_animatorData.Speed = Speed;
			_animatorData.CurrentLoopMode = CurrentLoopMode;
			_animatorData.CurrentFrame = CurrentFrame;
			_animatorData.ElapsedTime = CurrentElapsedTime;

			// Update SpriteRenderer properties
			_animatorData.Color = Color;
			_animatorData.LocalOffset = LocalOffset;
			_animatorData.Origin = Origin;
			_animatorData.LayerDepth = LayerDepth;
			_animatorData.RenderLayer = RenderLayer;
			_animatorData.Enabled = Enabled;
			_animatorData.SpriteEffects = SpriteEffects;

			// Update TextureFilePath for animator
			_animatorData.TextureFilePath = TextureFilePath;

			// Update animation events
			_animatorData.AnimationEvents = AnimationEvents != null
				? new List<AnimationEvent>(AnimationEvents)
				: new List<AnimationEvent>();

			_animatorData.LoadedLayers = LoadedLayers != null ? new List<string>(LoadedLayers) : new List<string>();
			_animatorData.LoadedTag = LoadedTag ?? "";

			return _animatorData;
		}
		set
		{
			if (value is SpriteAnimatorComponentData animatorData)
			{
				// Store the data first
				_animatorData = animatorData;

				// Apply SpriteRenderer properties
				Color = animatorData.Color;
				LocalOffset = animatorData.LocalOffset;
				Origin = animatorData.Origin;
				LayerDepth = animatorData.LayerDepth;
				RenderLayer = animatorData.RenderLayer;
				Enabled = animatorData.Enabled;
				SpriteEffects = animatorData.SpriteEffects;

				// Apply animation properties
				Speed = animatorData.Speed;

				// Apply animation events
				AnimationEvents = animatorData.AnimationEvents != null
					? new List<AnimationEvent>(animatorData.AnimationEvents)
					: new List<AnimationEvent>();

				// Apply TextureFilePath
				TextureFilePath = animatorData.TextureFilePath ?? "";

				LoadedLayers = animatorData.LoadedLayers != null ? new List<string>(animatorData.LoadedLayers) : new List<string>();
				LoadedTag = animatorData.LoadedTag ?? "";
			}
		}
	}

	public enum LoopMode
	{
		/// <summary>
		/// Play the sequence in a loop forever [A][B][C][A][B][C][A][B][C]...
		/// </summary>
		Loop,

		/// <summary>
		/// Play the sequence once [A][B][C] then pause and set time to 0 [A]
		/// </summary>
		Once,

		/// <summary>
		/// Plays back the animation once, [A][B][C]. When it reaches the end, it will keep playing the last frame and never stop playing
		/// </summary>
		ClampForever,

		/// <summary>
		/// Play the sequence in a ping pong loop forever [A][B][C][B][A][B][C][B]...
		/// </summary>
		PingPong,

		/// <summary>
		/// Play the sequence once forward then back to the start [A][B][C][B][A] then pause and set time to 0
		/// </summary>
		PingPongOnce
	}

	public enum State
	{
		None,
		Running,
		Paused,
		Completed
	}

	public enum PingPongLoopStates
	{
		Ping,
		Pong
	}

	/// <summary>
	/// fired when an animation completes, includes the animation name;
	/// </summary>
	public event Action<string> OnAnimationCompletedEvent;

	/// <summary>
	/// animation playback speed
	/// </summary>
	[Range(-10f, 10f)]
	public float Speed = 1;

	/// <summary>
	/// the current state of the animation
	/// </summary>
	public State AnimationState { get; private set; } = State.None;

	/// <summary>
	/// the current animation
	/// </summary>
	public SpriteAnimation CurrentAnimation { get; private set; }

	/// <summary>
	/// the name of the current animation
	/// </summary>
	public string CurrentAnimationName { get; private set; }

	/// <summary>
	/// index of the current frame in sprite array of the current animation
	/// </summary>
	public int CurrentFrame { get; set; }

	/// <summary>
	/// amount of frames in the current animation
	/// </summary>
	public int FrameCount { get; private set; }

	/// <summary>
	/// returns the total elapsed time of the animation.
	/// </summary>
	public float CurrentElapsedTime { get; private set; }

	/// <summary>
	/// Provides access to list of available animations
	/// </summary>
	public Dictionary<string, SpriteAnimation> Animations { get; private set; }
		= new();

	/// <summary>
	/// Mode of looping the animation.
	/// It can have 5 different values: Loop, Once, ClampForever, PingPong and PingPongOnce. Defaults to Loop.
	/// </summary>
	public LoopMode CurrentLoopMode { get; private set; }

	/// <summary>
	/// The amount of seconds remaining in the current frame
	/// </summary>
	public float FrameTimeLeft { get; private set; }


	public PingPongLoopStates PingPongLoopState { get; set; }

	private bool _pingPongOnceAnimationStarted = false;


	public SpriteAnimator()
	{
	}

	public SpriteAnimator(Sprite sprite)
	{
		SetSprite(sprite);
	}

#if DEBUG // Pause/UnPause animations in Play Mode
	public override void OnEnabled()
	{
		Core.OnChangedToPlayMode += UnPause;
	}

	public override void OnDisabled()
	{
		Core.OnChangedToPlayMode -= UnPause;
	}
#endif

	public virtual void Update()
	{
#if DEBUG // Pause in EditMode
		if(Core.IsEditMode && AnimationState == State.Running)
			Pause();
#endif
		if (AnimationState != State.Running || CurrentAnimation == null)
			return;

		CurrentElapsedTime += Time.DeltaTime;
		FrameTimeLeft -= Time.DeltaTime;

		// Execute LongAnimationEvent every tick if in range
		foreach (var evt in AnimationEvents)
		{
			if (evt is LongAnimationEvent longEvt)
			{
				if (CurrentFrame >= longEvt.StartFrame && CurrentFrame <= longEvt.EndFrame && !string.IsNullOrEmpty(longEvt.Name))
				{
					var key = (CurrentAnimationName, longEvt.Name);
					if (_animationEventSubscribers.TryGetValue(key, out var subscribers))
					{
						foreach (var subscriber in subscribers)
							subscriber?.Invoke();
					}
					longEvt.Callback?.Invoke();
				}
			}
		}

		// Fire normal AnimationEvents when the frame ENDS (before changing frame)
		if (ShouldChangeFrame())
		{
			foreach (var evt in AnimationEvents)
			{
				if (evt is LongAnimationEvent)
					continue; // Skip, handled above

				if (evt.StartFrame == CurrentFrame && !string.IsNullOrEmpty(evt.Name))
				{
					var key = (CurrentAnimationName, evt.Name);
					if (_animationEventSubscribers.TryGetValue(key, out var subscribers))
					{
						foreach (var subscriber in subscribers)
							subscriber?.Invoke();
					}
					evt.Callback?.Invoke();
				}
			}
			NextFrame();
		}
	}

	private bool LoadLastAnimation()
	{
		if (!string.IsNullOrEmpty(TextureFilePath) && !string.IsNullOrEmpty(LoadedTag) && LoadedLayers.Count > 0)
		{
			// Try to load the Aseprite file
			var asepriteFile = Entity?.Scene?.Content?.LoadAsepriteFile(TextureFilePath) ?? Core.Content.LoadAsepriteFile(TextureFilePath);
			if (asepriteFile == null)
				return false;

			// Check if the tag exists in the file
			bool tagExists = asepriteFile.Tags.Any(t => t.Name == LoadedTag);
			if (!tagExists)
			{
				// Optionally log a warning here
				//System.Console.WriteLine($"SpriteAnimator: Tag '{LoadedTag}' not found in '{TextureFilePath}'. Skipping animation load.");
				return false;
			}

			if (LoadedLayers != null && LoadedLayers.Count > 0)
			{
				AsepriteUtils.LoadAsepriteAnimationWithLayers(this, TextureFilePath, LoadedTag, null, LoadedLayers.ToArray());
			}
			else
			{
				AsepriteUtils.LoadAsepriteAnimation(this, TextureFilePath, LoadedTag);
			}

			return true;
		}

		return false;
	}

	/// <summary>
	/// Called when this component is added to an entity. 
	/// If we have saved texture file path data, load the image automatically.
	/// </summary>
	public override void OnAddedToEntity()
	{
		base.OnAddedToEntity();

		if (LoadLastAnimation())
		{
			Play(LoadedTag);
		}
	}

	public virtual void NextFrame()
	{
		switch (CurrentLoopMode)
		{
			case LoopMode.Loop:
				SetFrame((CurrentFrame + 1) % FrameCount);
				break;

			case LoopMode.Once:
			case LoopMode.ClampForever:
				var newFrame = CurrentFrame + 1;
				{
					if (newFrame >= FrameCount)
					{
						SetCompleted(CurrentLoopMode == LoopMode.Once);
					}
					else
					{
						SetFrame(newFrame);
					}
				}
				break;

			case LoopMode.PingPong:
				if (FrameCount == 1)
				{
					break;
				}

				ParsePingPongLoop();
				break;
			case LoopMode.PingPongOnce:
				if (CurrentFrame == 0)
				{
					if (_pingPongOnceAnimationStarted)
					{
						SetCompleted(true);
						break;
					}

					_pingPongOnceAnimationStarted = true;
				}

				ParsePingPongLoop();
				break;
		}
	}


	/// <summary>
	/// adds all the animations from the SpriteAtlas
	/// </summary>
	public SpriteAnimator AddAnimationsFromAtlas(SpriteAtlas atlas)
	{
		for (var i = 0; i < atlas.AnimationNames.Length; i++)
			Animations.Add(atlas.AnimationNames[i], atlas.SpriteAnimations[i]);
		return this;
	}

	/// <summary>
	/// Adds a SpriteAnimation
	/// </summary>
	public SpriteAnimator AddAnimation(string name, SpriteAnimation animation)
	{
		// if we have no sprite use the first frame we find
		if (Sprite == null && animation.Sprites.Length > 0)
			SetSprite(animation.Sprites[0]);
		Animations[name] = animation;
		return this;
	}

	public SpriteAnimator AddAnimation(string name, Sprite[] sprites, float fps = 10)
	{
		return AddAnimation(name, fps, sprites);
	}

	public SpriteAnimator AddAnimation(string name, float fps, params Sprite[] sprites)
	{
		AddAnimation(name, new SpriteAnimation(sprites, fps));
		return this;
	}

	/// <summary>
	/// checks to see if the animation is playing (i.e. the animation is active. it may still be in the paused state)
	/// </summary>
	public bool IsAnimationActive(string name)
	{
		return CurrentAnimation != null && CurrentAnimationName.Equals(name);
	}

	/// <summary>
	/// checks to see if the CurrentAnimation is running
	/// </summary>
	public bool IsRunning => AnimationState == State.Running;

	/// <summary>
	/// plays the animation with the given name and frame. If no loopMode is specified it is defaults to Loop
	/// </summary>
	public void Play(string name, LoopMode loopMode = LoopMode.Loop, int frameIndex = 0)
	{
		CurrentElapsedTime = 0;
		CurrentAnimation = Animations[name];
		CurrentAnimationName = name;
		FrameCount = CurrentAnimation.FrameRates.Length;

		SetFrame(frameIndex);

		CurrentLoopMode = loopMode;
		AnimationState = State.Running;
	}

	/// <summary>
	/// pauses the animator
	/// </summary>
	public void Pause()
	{
		AnimationState = State.Paused;
	}

	/// <summary>
	/// unpauses the animator
	/// </summary>
	public void UnPause()
	{
		AnimationState = State.Running;
	}

	/// <summary>
	/// stops the current animation and nulls it out
	/// </summary>
	public void Stop()
	{
		CurrentAnimation = null;
		CurrentAnimationName = null;
		CurrentFrame = 0;
		CurrentElapsedTime = 0;
		AnimationState = State.None;
	}

	/// <summary>
	/// Sets the current frame for the animation
	/// </summary>
	/// <param name="frameIndex">Index of the desired frame</param>
	public void SetFrame(int frameIndex)
	{
		frameIndex = Math.Clamp(frameIndex, 0, CurrentAnimation.Sprites.Length - 1);
		CurrentFrame = frameIndex;
		Sprite = CurrentAnimation.Sprites[frameIndex];
		FrameTimeLeft = ConvertFrameRateToSeconds(CurrentAnimation.FrameRates[frameIndex]);
	}

	/// <summary>
	/// Sets the animation as completed
	/// </summary>
	/// <param name="returnToFirstFrame">If the animation should return to the first frame before finishing</param>
	public void SetCompleted(bool returnToFirstFrame = false)
	{
		if (returnToFirstFrame) SetFrame(0);

		PingPongLoopState = PingPongLoopStates.Ping;
		_pingPongOnceAnimationStarted = false;

		CurrentElapsedTime = 0;
		AnimationState = State.Completed;
		OnAnimationCompletedEvent?.Invoke(CurrentAnimationName);
	}

	/// <summary>
	/// Checks if it needs to change the current animation frame
	/// </summary>
	/// <returns>True if it does need to change frame, false otherwise</returns>
	private bool ShouldChangeFrame()
	{
		return FrameTimeLeft <= 0;
	}

	/// <summary>
	/// Converts an animation frame rate (1/60s) to seconds
	/// </summary>
	/// <param name="frameRate"></param>
	/// <returns>The number of seconds as a float</returns>
	private float ConvertFrameRateToSeconds(float frameRate)
	{
		return 1 / (frameRate * Speed);
	}

	private void ParsePingPongLoop()
	{
		switch (PingPongLoopState)
		{
			case PingPongLoopStates.Ping:
				ParsePingLoop();
				break;
			case PingPongLoopStates.Pong:
				ParsePongLoop();
				break;
		}
	}

	private void ParsePingLoop()
	{
		var newFrame = CurrentFrame + 1;
		if (newFrame >= FrameCount)
		{
			PingPongLoopState = PingPongLoopStates.Pong;
			ParsePongLoop();
		}
		else
		{
			SetFrame(newFrame);
		}
	}

	private void ParsePongLoop()
	{
		var newFrame = CurrentFrame - 1;
		if (newFrame < 0)
		{
			PingPongLoopState = PingPongLoopStates.Ping;
			ParsePingLoop();
		}
		else
		{
			SetFrame(newFrame);
		}
	}

	/// <summary>
	/// WARNING: Can't calculate NormalizedTime for PingPong type of loop! Will return NULL in that case!
	/// </summary>
	public float? GetNormalizedTime()
	{
		if (CurrentLoopMode == LoopMode.PingPong || CurrentLoopMode == LoopMode.PingPongOnce)
		{
			System.Console.WriteLine($"Can't calculate NormalizedTime for PingPong type of Loop! Animation: {CurrentAnimationName}");
			return null;
		}

		return (CurrentFrame + 1) / (float)CurrentAnimation.Sprites.Length;
	}

	/// <summary>
	/// Loads an animation from an Aseprite file using AsepriteUtils. Only ".ase" or ".aseprite" files are accepted.
	/// </summary>
	/// <param name="animationTagName">The tag name of the animation in the Aseprite file.</param>
	/// <param name="callableAnimationName">The name to use for the animation in the animator (optional).</param>
	/// <param name="layerName">Optional layer name to filter the animation (null for all layers).</param>
	/// <exception cref="ArgumentException">Thrown if the file type is not supported.</exception>
	public void LoadAsepriteAnimation(string animationTagName, string callableAnimationName = null, string layerName = null)
	{
		if (string.IsNullOrEmpty(TextureFilePath))
			throw new ArgumentException("TextureFilePath must be set before loading an animation.");

		var ext = System.IO.Path.GetExtension(TextureFilePath).ToLowerInvariant();
		if (ext != ".ase" && ext != ".aseprite")
			throw new ArgumentException("Only .ase or .aseprite files are supported for SpriteAnimator animation loading.");

		AsepriteUtils.LoadAsepriteAnimation(Entity, TextureFilePath, animationTagName, callableAnimationName, layerName);
	}

	public void SubscribeToEvent(string animationName, string eventName, Action callback)
	{
	    // Check if the event exists for the given animation
	    bool eventExists = AnimationEvents.Any(e =>
	        e.AnimationName == animationName && e.Name == eventName);

	    if (!eventExists)
	        throw new InvalidOperationException(
	            $"AnimationEvent '{eventName}' does not exist for animation '{animationName}'.");

	    var key = (animationName, eventName);
	    if (!_animationEventSubscribers.TryGetValue(key, out var list))
	    {
	        list = new List<Action>();
	        _animationEventSubscribers[key] = list;
	    }
	    list.Add(callback);
	}

	public override SpriteRenderer SetSprite(Sprite sprite)
	{
		base.SetSprite(sprite);
		return this;
	}
}