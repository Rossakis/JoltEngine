using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Content;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Voltage.Aseprite;
using Voltage.BitmapFonts;
using Voltage.ParticleDesigner;
using Voltage.Sprites;
using Voltage.Textures;
using Voltage.Tiled;
using Voltage.Tilesets;
using Voltage.Utils;


namespace Voltage.Systems;

/// <summary>
/// ContentManager subclass that also manages Effects from ogl files. Adds asynchronous loading of assets as well.
/// </summary>
public class VoltageContentManager : ContentManager
{
	private Dictionary<string, Effect> _loadedEffects = new();

	private List<IDisposable> _disposableAssets;

	private List<IDisposable> DisposableAssets
	{
		get
		{
			if (_disposableAssets == null)
			{
				var fieldInfo = ReflectionUtils.GetFieldInfo(typeof(ContentManager), "disposableAssets");
				_disposableAssets = fieldInfo.GetValue(this) as List<IDisposable>;
			}

			return _disposableAssets;
		}
	}

#if FNA
		Dictionary<string, object> _loadedAssets;
		Dictionary<string, object> LoadedAssets
		{
			get
			{
				if (_loadedAssets == null)
				{
					var fieldInfo = ReflectionUtils.GetFieldInfo(typeof(ContentManager), "loadedAssets");
					_loadedAssets = fieldInfo.GetValue(this) as Dictionary<string, object>;
				}
				return _loadedAssets;
			}
		}
#endif


	public VoltageContentManager(IServiceProvider serviceProvider, string rootDirectory) : base(serviceProvider,
		rootDirectory)
	{
		UseCompiledRoot();
	}

	public VoltageContentManager(IServiceProvider serviceProvider) : base(serviceProvider)
	{
		UseCompiledRoot();
	}

	public VoltageContentManager() : base(((Game)Core._instance).Services, ((Game)Core._instance).Content.RootDirectory)
	{
		UseCompiledRoot();
	}

	/// <summary>Compiled assets live under Content; the root is fixed here, once, so the legacy extension-less .xnb path never moves mid-session.</summary>
	private void UseCompiledRoot()
	{
		if (string.IsNullOrEmpty(RootDirectory) && Voltage.Assets.CompiledContentIndex.Count > 0)
			RootDirectory = "Content";
	}

	#region Strongly Typed Loaders

	/// <summary>
	/// loads a Texture2D either from xnb or directly from a png/jpg. Note that xnb files should not contain the .xnb file
	/// extension or be preceded by "Content" in the path. png/jpg files should have the file extension and have an absolute
	/// path or a path starting with "Content".
	/// </summary>
	public Texture2D LoadTexture(string name, bool premultiplyAlpha = false)
	{
		// no file extension. Assumed to be an xnb so let ContentManager load it
		if (string.IsNullOrEmpty(Path.GetExtension(name)))
			return Load<Texture2D>(name);

		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is Texture2D tex)
				return tex;

		if (TryLoadCompiled<Texture2D>(name, out var compiled))
		{
			if (premultiplyAlpha)
				TextureUtils.PremultiplyAlpha(compiled);
			compiled.Name = name;
			LoadedAssets[name] = compiled;
			return compiled;
		}

		using (var stream = File.OpenRead(ResolveContentPath(name)))
		{
			var texture = premultiplyAlpha
				? TextureUtils.TextureFromStreamPreMultiplied(stream)
				: Texture2D.FromStream(Core.GraphicsDevice, stream);
			texture.Name = name;
			LoadedAssets[name] = texture;
			DisposableAssets.Add(texture);

			return texture;
		}
	}

	/// <summary>
	/// loads a SoundEffect either from xnb or directly from a wav. Note that xnb files should not contain the .xnb file
	/// extension or be preceded by "Content" in the path. wav files should have the file extension and have an absolute
	/// path or a path starting with "Content".
	/// </summary>
	public SoundEffect LoadSoundEffect(string name)
	{
		var extension = Path.GetExtension(name);

		// no file extension. Assumed to be an xnb so let ContentManager load it
		if (string.IsNullOrEmpty(extension))
			return Load<SoundEffect>(name);

		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is SoundEffect cached)
				return cached;

		if (TryLoadCompiled<SoundEffect>(name, out var compiled))
		{
			LoadedAssets[name] = compiled;
			return compiled;
		}

		var path = ResolveContentPath(name);

		// Dispatch by format. WAV loads natively; compressed formats are decoded to PCM by pure-managed
		// decoders so they end up as ordinary SoundEffects on the engine mixer path (see AudioDecoders).
		SoundEffect sfx;
		switch (extension.ToLowerInvariant())
		{
			case ".ogg":
				sfx = Voltage.Audio.AudioDecoders.DecodeOgg(path);
				break;
			case ".mp3":
				sfx = Voltage.Audio.AudioDecoders.DecodeMp3(path);
				break;
			default: // .wav (and any other stream SoundEffect can parse natively)
				using (var stream = File.OpenRead(path))
					sfx = SoundEffect.FromStream(stream);
				break;
		}

		LoadedAssets[name] = sfx;
		DisposableAssets.Add(sfx);
		return sfx;
	}

	/// <summary>
	/// loads a Tiled map
	/// </summary>
	public TmxMap LoadTiledMap(string name)
	{
		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is TmxMap map)
				return map;

		try
		{
			var tiledMap = new TmxMap().LoadTmxMap(name, this);

			LoadedAssets[name] = tiledMap;
			DisposableAssets.Add(tiledMap);

			return tiledMap;
		}
		catch (Exception e)
		{
			Debug.Error(e.ToString());
			throw;
		}
	}

	/// <summary>
	/// Loads a ParticleDesigner pex file
	/// </summary>
	public Particles.ParticleEmitterConfig LoadParticleEmitterConfig(string name)
	{
		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is Particles.ParticleEmitterConfig config)
				return config;

		var emitterConfig = ParticleEmitterConfigLoader.Load(name);

		LoadedAssets[name] = emitterConfig;
		DisposableAssets.Add(emitterConfig);

		return emitterConfig;
	}

	/// <summary>
	/// Loads a SpriteAtlas created with the Sprite Atlas Packer tool
	/// </summary>
	public SpriteAtlas LoadSpriteAtlas(string name, bool premultiplyAlpha = false)
	{
		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is SpriteAtlas spriteAtlas)
				return spriteAtlas;

		var atlas = SpriteAtlasLoader.ParseSpriteAtlas(name, premultiplyAlpha);

		LoadedAssets.Add(name, atlas);
		DisposableAssets.Add(atlas);

		return atlas;
	}

	/// <summary>
	/// Loads a BitmapFont
	/// </summary>
	public BitmapFont LoadBitmapFont(string name, bool premultiplyAlpha = false)
	{
		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is BitmapFont bmFont)
				return bmFont;

		if (TryLoadCompiled<BitmapFont>(name, out var compiled))
		{
			if (premultiplyAlpha)
				foreach (var page in compiled.Textures)
					TextureUtils.PremultiplyAlpha(page);
			LoadedAssets[name] = compiled;
			return compiled;
		}

		var font = BitmapFontLoader.LoadFontFromFile(name, premultiplyAlpha);

		LoadedAssets.Add(name, font);
		DisposableAssets.Add(font);

		return font;
	}

	/// <summary>
	/// Loads the contents of an Aseprite (.ase/.aseprite) file.
	/// </summary>
	/// <param name="name">The content path name of the Aseprite file to load.</param>
	/// <returns>
	/// A new instance of the <see cref="AsepriteFile"/> class initialized with the data read from the Aseprite
	/// file.
	/// </returns>
	public AsepriteFile LoadAsepriteFile(string name)
	{
		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is AsepriteFile aseFile)
				return aseFile;

		if (TryLoadCompiled<AsepriteFile>(name, out var compiled))
		{
			LoadedAssets[name] = compiled;
			return compiled;
		}

		var asepriteFile = AsepriteFileLoader.Load(ResolveContentPath(name));
		LoadedAssets.Add(name, asepriteFile);
		return asepriteFile;
	}

	/// <summary>Loads a <c>.vtileset</c> asset.</summary>
	public TilesetAsset LoadTileset(string name)
	{
		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is TilesetAsset tileset)
				return tileset;

		var loaded = TilesetAssetIO.Load(ResolveContentPath(name));
		LoadedAssets.Add(name, loaded);
		return loaded;
	}

	/// <summary>
	/// loads a json file into a string.
	/// </summary>
	/// <returns>The json string.</returns>
	/// <param name="name">The json filename.</param>
	public string LoadJson(string name)
	{
		if (LoadedAssets.TryGetValue(name, out var asset))
			if (asset is string json)
				return json;

		using (var stream = File.OpenRead(ResolveContentPath(name)))
		{
			using (var reader = new StreamReader(stream))
			{
				var jsonString = reader.ReadToEnd();
				LoadedAssets.Add(name, jsonString);
				return jsonString;
			}
		}
	}

	/// <summary>
	/// loads an ogl effect directly from file and handles disposing of it when the ContentManager is disposed. Name should be the path
	/// relative to the Content folder or including the Content folder.
	/// </summary>
	/// <returns>The effect.</returns>
	/// <param name="name">Name.</param>
	public Effect LoadEffect(string name)
	{
		return LoadEffect<Effect>(name);
	}

	/// <summary>
	/// loads an embedded voltage effect. These are any of the Effect subclasses in the voltage/Graphics/Effects folder.
	/// Note that this will return a unique instance if you attempt to load the same Effect twice to avoid Effect duplication.
	/// </summary>
	/// <returns>The voltage effect.</returns>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	public T LoadVoltageEffect<T>() where T : Effect, new()
	{
		var cacheKey = typeof(T).Name + "-" + Utils.Utils.RandomString(5);
		var effect = new T();
		effect.Name = cacheKey;
		_loadedEffects[cacheKey] = effect;

		return effect;
	}

	/// <summary>
	/// loads an ogl effect directly from file and handles disposing of it when the ContentManager is disposed. Name should the the path
	/// relative to the Content folder or including the Content folder. Effects must have a constructor that accepts GraphicsDevice and
	/// byte[]. Note that this will return a unique instance if you attempt to load the same Effect twice to avoid Effect duplication.
	/// </summary>
	/// <returns>The effect.</returns>
	/// <param name="name">Name.</param>
	public T LoadEffect<T>(string name) where T : Effect
	{
		// make sure the effect has the proper root directory
		if (!name.StartsWith(RootDirectory))
			name = RootDirectory + "/" + name;

		var bytes = EffectResource.GetFileResourceBytes(name);

		return LoadEffect<T>(name, bytes);
	}

	/// <summary>
	/// loads an ogl effect directly from its bytes and handles disposing of it when the ContentManager is disposed. Name should the the path
	/// relative to the Content folder or including the Content folder. Effects must have a constructor that accepts GraphicsDevice and
	/// byte[]. Note that this will return a unique instance if you attempt to load the same Effect twice to avoid Effect duplication.
	/// </summary>
	/// <returns>The effect.</returns>
	/// <param name="name">Name.</param>
	public T LoadEffect<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(string name, byte[] effectCode) where T : Effect
	{
		var effect = Activator.CreateInstance(typeof(T), Core.GraphicsDevice, effectCode) as T;
		effect.Name = name + "-" + Utils.Utils.RandomString(5);
		_loadedEffects[effect.Name] = effect;

		return effect;
	}

	/// <summary>
	/// loads and manages any Effect that is built-in to MonoGame such as BasicEffect, AlphaTestEffect, etc. Note that this will
	/// return a unique instance if you attempt to load the same Effect twice. If you intend to use the same Effect in multiple locations
	/// keep a reference to it and use it directly.
	/// </summary>
	/// <returns>The mono game effect.</returns>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	public T LoadMonoGameEffect<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : Effect
	{
		var effect = Activator.CreateInstance(typeof(T), Core.GraphicsDevice) as T;
		effect.Name = typeof(T).Name + "-" + Utils.Utils.RandomString(5);
		_loadedEffects[effect.Name] = effect;

		return effect;
	}

	#endregion

	#region Reference-Based Loading

	/// <summary>
	/// Loads an asset for an already-resolved absolute <paramref name="resolvedPath"/> (extension
	/// included) and its pre-computed extension-less <paramref name="contentName"/>, choosing the
	/// matching strongly-typed loader for <typeparamref name="T"/> at runtime. For the dual-format
	/// types (<see cref="Texture2D"/>, <see cref="SoundEffect"/>) <paramref name="rawExists"/>
	/// selects between streaming the raw source file and loading a compiled <c>.xnb</c>; every other
	/// supported type is a raw-file parser. Unmapped types fall through to the MonoGame content
	/// pipeline. The per-type loader is cached, and the underlying loaders cache the loaded asset,
	/// so this is allocation-, reflection- and lookup-free on the hot path.
	/// </summary>
	public T LoadByType<T>(string resolvedPath, string contentName, bool rawExists)
	{
		var loader = TypedAssetLoader<T>.Loader;
		if (loader != null)
			return (T)loader(this, resolvedPath, contentName, rawExists);

		// No dedicated loader for T. It may still be a valid MonoGame content-pipeline asset
		// (SpriteFont, Model, an .xnb-built type, …), so try that path — but if it fails, replace
		// the pipeline's cryptic "content file not found" with a message that explains exactly
		// what is wrong and how to load this type properly.
		if (string.IsNullOrEmpty(contentName))
		{
			Debug.Error(UnsupportedAssetMessage<T>(resolvedPath, "its path could not be resolved to a content name"));
			return default;
		}

		try
		{
			return Load<T>(contentName);
		}
		catch (Exception ex)
		{
			Debug.Error(UnsupportedAssetMessage<T>(resolvedPath, ex.Message));
			return default;
		}
	}

	// Builds a precise, actionable diagnostic for a LoadAsset/LoadByType call that could not be
	// satisfied — distinguishing "this type is not loadable this way" from a plain missing file,
	// and pointing at the correct API for the requested type.
	private static string UnsupportedAssetMessage<T>(string resolvedPath, string reason)
	{
		var type = typeof(T);
		var sb = new StringBuilder();
		sb.Append($"LoadAsset<{type.Name}> failed for '{resolvedPath}': {reason}. ");

		if (typeof(Effect).IsAssignableFrom(type))
		{
			sb.Append("Effects cannot be loaded through an AssetReference — they are not file-backed assets. Use ")
			  .Append($"Core.Content.LoadMonoGameEffect<{type.Name}>() for a built-in MonoGame effect, ")
			  .Append($"LoadVoltageEffect<{type.Name}>() for a built-in Voltage effect, or ")
			  .Append("LoadEffect(name) for an effect file.");
		}
		else
		{
			sb.Append($"'{type.Name}' has no dedicated Voltage loader and is not a compiled content-pipeline asset. ")
			  .Append($"AssetReference-loadable types are: {AssetLoaderTable.SupportedTypeNames}. ")
			  .Append("To support a new type, add an entry to VoltageContentManager's asset loader table, ")
			  .Append("or load it directly with the matching Core.Content.Load* method.");
		}

		return sb.ToString();
	}

	// Resolved once per closed generic type T (static field init), so every subsequent
	// LoadByType<T> call is a direct static read plus a delegate invocation — no dictionary
	// lookup, no reflection, and no boxing for these reference-type assets.
	private static class TypedAssetLoader<T>
	{
		public static readonly Func<VoltageContentManager, string, string, bool, object> Loader =
			AssetLoaderTable.Resolve(typeof(T));
	}

	// Single source of truth for "engine asset type -> loader". Add a line to support a new type.
	private static class AssetLoaderTable
	{
		private static readonly Dictionary<Type, Func<VoltageContentManager, string, string, bool, object>> Map = new()
		{
			// Dual-format: the source path when it exists or an asset build compiled it, else a legacy .xnb name.
			[typeof(Texture2D)]   = static (c, path, name, raw) => c.LoadTexture(raw || IsCompiled(path) ? path : name),
			[typeof(SoundEffect)] = static (c, path, name, raw) => c.LoadSoundEffect(raw || IsCompiled(path) ? path : name),

			// Raw-only parsers: always stream the resolved source file.
			[typeof(SpriteAtlas)]                      = static (c, path, name, raw) => c.LoadSpriteAtlas(path),
			[typeof(BitmapFont)]                       = static (c, path, name, raw) => c.LoadBitmapFont(path),
			[typeof(AsepriteFile)]                     = static (c, path, name, raw) => c.LoadAsepriteFile(path),
			[typeof(TmxMap)]                           = static (c, path, name, raw) => c.LoadTiledMap(path),
			[typeof(TilesetAsset)]                     = static (c, path, name, raw) => c.LoadTileset(path),
			[typeof(Particles.ParticleEmitterConfig)]  = static (c, path, name, raw) => c.LoadParticleEmitterConfig(path),
			[typeof(Effect)]                           = static (c, path, name, raw) => c.LoadEffect(path),
			[typeof(string)]                           = static (c, path, name, raw) => c.LoadJson(path),
		};

		private static readonly Func<VoltageContentManager, string, string, bool, object> DataAssetLoader =
			static (c, path, name, raw) => Voltage.Data.DataAssetCache.GetByPath(path);

		private static bool IsCompiled(string path) => Voltage.Assets.CompiledContentIndex.TryGetAssetName(path, out _);

		// Human-readable list of AssetReference-loadable types, surfaced in diagnostics.
		public static readonly string SupportedTypeNames =
			string.Join(", ", Map.Keys.Select(t => t == typeof(string) ? "string (JSON)" : t.Name))
			+ ", and any DataAsset subclass";

		public static Func<VoltageContentManager, string, string, bool, object> Resolve(Type t)
		{
			if (Map.TryGetValue(t, out var loader))
				return loader;

			return typeof(Voltage.Data.DataAsset).IsAssignableFrom(t) ? DataAssetLoader : null;
		}
	}

	#endregion

	#region Compiled Content

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> CompiledFailures = new(StringComparer.OrdinalIgnoreCase);

	static VoltageContentManager()
	{
		Voltage.Assets.CompiledContentIndex.OnReset += CompiledFailures.Clear;
	}

	/// <summary>Loads the .xnb an asset build compiled for this source path; false when none exists or it fails, so the raw file is used instead.</summary>
	private bool TryLoadCompiled<T>(string name, out T asset) where T : class
	{
		asset = null;
		if (!Voltage.Assets.CompiledContentIndex.TryGetAssetName(name, out var assetName))
			return false;

		try
		{
			asset = Load<T>(assetName);
			// The caller caches under the source path; drop the pipeline's own key so eviction and reload see one entry.
			LoadedAssets.Remove(assetName);
			LoadedAssets.Remove(assetName.Replace('\\', '/'));
			return asset != null;
		}
		catch (Exception ex)
		{
			if (CompiledFailures.TryAdd(name, 0))
				Debug.Warn($"[Content] compiled asset '{assetName}' for '{name}' failed to load, using the source file: {ex.Message}");
			asset = null;
			return false;
		}
	}

	#endregion

	#region Content Path Resolution

	/// <summary>
	/// Root directory used to resolve relative content paths.
	/// <para>
	/// Set this to the game project root when a project is loaded so that paths
	/// such as "Content/Characters/foo.aseprite" resolve against the project
	/// rather than the application base directory.
	/// </para>
	/// Defaults to <see cref="AppContext.BaseDirectory"/> for standalone builds.
	/// </summary>
	public static string ContentRoot { get; set; } = AppContext.BaseDirectory;

	/// <summary>
	/// Resolves a relative content path to absolute using <see cref="ContentRoot"/>.
	/// Absolute paths are returned unchanged.
	/// </summary>
	/// <remarks>
	/// Separators are normalized to forward slashes BEFORE the path is combined with
	/// <see cref="ContentRoot"/>. Asset paths stored in scene/asset data authored on
	/// Windows use backslashes (e.g. "Content\Characters\foo.aseprite"). On Linux/macOS
	/// a backslash is a literal filename character, so <see cref="Path.GetFullPath(string,string)"/>
	/// would carry it through and the file would not be found.
	/// </remarks>
	/// <summary>Drops cached entries for this file so the next Load re-reads it. Disposes nothing.</summary>
	public bool EvictCachedAsset(string name)
	{
		if (string.IsNullOrEmpty(name))
			return false;

		var target = ResolveContentPath(name);
		var removed = false;

		foreach (var key in LoadedAssets.Keys.ToList())
		{
			if (!SameContentFile(key, name, target))
				continue;

			LoadedAssets.Remove(key);
			removed = true;
		}

		return removed;
	}

	private static bool SameContentFile(string key, string name, string resolvedTarget)
	{
		if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
			return true;

		try
		{
			return string.Equals(ResolveContentPath(key), resolvedTarget, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static string ResolveContentPath(string name)
	{
		if (string.IsNullOrEmpty(name))
			return name;

		name = name.Replace('\\', '/');
		return Path.IsPathRooted(name) ? name : Path.GetFullPath(name, ContentRoot);
	}

	#endregion

	/// <summary>
	/// loads an asset on a background thread with optional callback for when it is loaded. The callback will occur on the main thread.
	/// </summary>
	/// <param name="assetName">Asset name.</param>
	/// <param name="onLoaded">On loaded.</param>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	public void LoadAsync<T>(string assetName, Action<T> onLoaded = null)
	{
		var syncContext = SynchronizationContext.Current;
		Task.Run(() =>
		{
			var asset = Load<T>(assetName);

			// if we have a callback do it on the main thread
			if (onLoaded != null) syncContext.Post(d => { onLoaded(asset); }, null);
		});
	}

	/// <summary>
	/// loads an asset on a background thread with optional callback that includes a context parameter for when it is loaded.
	/// The callback will occur on the main thread.
	/// </summary>
	/// <param name="assetName">Asset name.</param>
	/// <param name="onLoaded">On loaded.</param>
	/// <param name="context">Context.</param>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	public void LoadAsync<T>(string assetName, Action<object, T> onLoaded = null, object context = null)
	{
		var syncContext = SynchronizationContext.Current;
		Task.Run(() =>
		{
			var asset = Load<T>(assetName);

			if (onLoaded != null) syncContext.Post(d => { onLoaded(context, asset); }, null);
		});
	}

	/// <summary>
	/// loads a group of assets on a background thread with optional callback for when it is loaded
	/// </summary>
	/// <param name="assetNames">Asset names.</param>
	/// <param name="onLoaded">On loaded.</param>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	public void LoadAsync<T>(string[] assetNames, Action onLoaded = null)
	{
		var syncContext = SynchronizationContext.Current;
		Task.Run(() =>
		{
			for (var i = 0; i < assetNames.Length; i++)
				Load<T>(assetNames[i]);

			// if we have a callback do it on the main thread
			if (onLoaded != null) syncContext.Post(d => { onLoaded(); }, null);
		});
	}

	/// <summary>
	/// removes assetName from LoadedAssets and Disposes of it
	/// disposeableAssets List.
	/// </summary>
	/// <param name="assetName">Asset name.</param>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	public void UnloadAsset<T>(string assetName) where T : class, IDisposable
	{
		if (IsAssetLoaded(assetName))
			try
			{
				// first fetch the actual asset. we already know its loaded so we'll grab it directly
				var assetToRemove = LoadedAssets[assetName];
				for (var i = 0; i < DisposableAssets.Count; i++)
				{
					// see if the asset is disposeable. If so, find and dispose of it.
					var typedAsset = DisposableAssets[i] as T;
					if (typedAsset != null && typedAsset == assetToRemove)
					{
						typedAsset.Dispose();
						DisposableAssets.RemoveAt(i);
						break;
					}
				}

				LoadedAssets.Remove(assetName);
			}
			catch (Exception e)
			{
				Debug.Error($"Could not unload asset {assetName}. {e}");
			}
	}

	/// <summary>
	/// unloads an Effect that was loaded via loadEffect, loadvoltageEffect or loadMonoGameEffect
	/// </summary>
	/// <param name="effectName">Effect.name</param>
	public bool UnloadEffect(string effectName)
	{
		if (_loadedEffects.ContainsKey(effectName))
		{
			_loadedEffects[effectName].Dispose();
			_loadedEffects.Remove(effectName);
			return true;
		}

		return false;
	}

	/// <summary>
	/// unloads an Effect that was loaded via loadEffect, loadvoltageEffect or loadMonoGameEffect
	/// </summary>
	public bool UnloadEffect(Effect effect)
	{
		return UnloadEffect(effect.Name);
	}

	/// <summary>
	/// checks to see if an asset with assetName is loaded
	/// </summary>
	/// <returns><c>true</c> if this instance is asset loaded the specified assetName; otherwise, <c>false</c>.</returns>
	/// <param name="assetName">Asset name.</param>
	public bool IsAssetLoaded(string assetName)
	{
		return LoadedAssets.ContainsKey(assetName);
	}

	/// <summary>
	/// provides a string suitable for logging with all the currently loaded assets and effects
	/// </summary>
	/// <returns>The loaded assets.</returns>
	internal string LogLoadedAssets()
	{
		var builder = new StringBuilder();
		foreach (var asset in LoadedAssets.Keys)
			builder.AppendFormat("{0}: ({1})\n", asset, LoadedAssets[asset].GetType().Name);

		foreach (var asset in _loadedEffects.Keys)
			builder.AppendFormat("{0}: ({1})\n", asset, _loadedEffects[asset].GetType().Name);

		return builder.ToString();
	}

	/// <summary>
	/// reverse lookup. Gets the asset path given the asset. This is useful for making editor and non-runtime stuff.
	/// </summary>
	/// <param name="asset"></param>
	/// <returns></returns>
	public string GetPathForLoadedAsset(object asset)
	{
		if (LoadedAssets.ContainsValue(asset))
			foreach (var kv in LoadedAssets)
				if (kv.Value == asset)
					return kv.Key;

		return null;
	}

	/// <summary>
	/// override that disposes of all loaded Effects
	/// </summary>
	public override void Unload()
	{
		base.Unload();

		foreach (var key in _loadedEffects.Keys)
			_loadedEffects[key].Dispose();

		_loadedEffects.Clear();
	}
}

/// <summary>
/// the only difference between this class and VoltageContentManager is that this one can load embedded resources from the voltage.dll
/// </summary>
internal sealed class VoltageGlobalContentManager : VoltageContentManager
{
	public VoltageGlobalContentManager(IServiceProvider serviceProvider, string rootDirectory) : base(serviceProvider,
		rootDirectory)
	{
	}

	protected override Stream OpenStream(string assetName)
	{
		if (assetName.StartsWith("voltage://"))
		{
			var assembly = GetType().Assembly;
			return assembly.GetManifestResourceStream(assetName.Substring(6));
		}

		return base.OpenStream(assetName);
	}
}
