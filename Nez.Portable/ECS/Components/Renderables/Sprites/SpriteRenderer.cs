using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Nez.Data;
using Nez.DeferredLighting;
using Nez.Materials;
using Nez.Systems;
using Nez.Textures;
using Nez.Tiled;
using Nez.Utils;
using System;
using System.IO;
using System.Reflection.Metadata;


namespace Nez.Sprites
{
	/// <summary>
	/// the most basic and common Renderable. Renders a Sprite/Texture.
	/// </summary>
	public class SpriteRenderer : RenderableComponent
	{
		/// <summary>
		/// Serializable data for SpriteRenderer component.
		/// </summary>
		public class SpriteRendererComponentData : ComponentData
		{
			public string TextureFilePath = "";
			public string NormalMapFilePath = ""; 
			
			// Store color as individual RGBA components for proper serialization
			public byte ColorR = 255;
			public byte ColorG = 255;
			public byte ColorB = 255;
			public byte ColorA = 255;
			
			public Vector2 LocalOffset = Vector2.Zero;
			public Vector2 Origin = Vector2.Zero;
			public float LayerDepth = 0f;
			public int RenderLayer = 0;
			public SpriteEffects SpriteEffects = SpriteEffects.None;
			public ImageFileType FileType = ImageFileType.None;
			public ImageFileType NormalMapFileType = ImageFileType.None;
			
			// File type specific data
			public AsepriteImageData? AsepriteData = null;
			public TiledImageData? TiledData = null;

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

			public enum ImageFileType
			{
				None = 0,
				Png = 1,
				Aseprite = 2,
				Tiled = 3
			}

			public struct AsepriteImageData
			{
				public string LayerName;
				public int FrameNumber;
				public bool OnlyVisibleLayers;
				public bool IncludeBackgroundLayer;

				public AsepriteImageData(string layerName = null, int frameNumber = 0, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false)
				{
					LayerName = layerName ?? "";
					FrameNumber = frameNumber;
					OnlyVisibleLayers = onlyVisibleLayers;
					IncludeBackgroundLayer = includeBackgroundLayer;
				}
			}

			public struct TiledImageData
			{
				public string ImageLayerName;

				public TiledImageData(string imageLayerName = null)
				{
					ImageLayerName = imageLayerName ?? "";
				}
			}

			public SpriteRendererComponentData()
			{
				// Ensure all properties have explicit default values
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
				FileType = ImageFileType.None;
				AsepriteData = null;
				TiledData = null;
				NormalMapFilePath = ""; 
				NormalMapFileType = ImageFileType.None; 
			}

			public SpriteRendererComponentData(SpriteRenderer renderer)
			{
				// Always set all properties, even if they're defaults
				TextureFilePath = renderer.Sprite?.Texture2D?.Name ?? "";
				
				// Store color components
				Color = renderer.Color;
				
				LocalOffset = renderer.LocalOffset;
				Origin = renderer.Origin;
				LayerDepth = renderer.LayerDepth;
				RenderLayer = renderer.RenderLayer;
				Enabled = renderer.Enabled;
				SpriteEffects = renderer.SpriteEffects;
				
				// Copy the existing data from the renderer's _data field
				if (renderer._data != null)
				{
					FileType = renderer._data.FileType;
					AsepriteData = renderer._data.AsepriteData;
					TiledData = renderer._data.TiledData;
					
					// Preserve existing TextureFilePath if current sprite is null but we had data
					if (string.IsNullOrEmpty(TextureFilePath) && !string.IsNullOrEmpty(renderer._data.TextureFilePath))
					{
						TextureFilePath = renderer._data.TextureFilePath;
					}
					
					
					NormalMapFilePath = renderer._data.NormalMapFilePath;
					NormalMapFileType = renderer._data.NormalMapFileType;
				}
				else
				{
					// Determine file type from texture name/path if no existing data
					if (!string.IsNullOrEmpty(TextureFilePath))
					{
						var extension = Path.GetExtension(TextureFilePath).ToLower();
						FileType = extension switch
						{
							".png" => ImageFileType.Png, 
							".ase" or ".aseprite" => ImageFileType.Aseprite,
							".tmx" => ImageFileType.Tiled,
							_ => ImageFileType.None
						};
					}
					else
					{
						FileType = ImageFileType.None;
					}
					
					AsepriteData = null;
					TiledData = null;
				}
			}

			/// <summary>
			/// Sets Aseprite-specific loading parameters
			/// </summary>
			public void SetAsepriteData(string layerName = null, int frameNumber = 0, bool onlyVisibleLayers = true, bool includeBackgroundLayer = false)
			{
				FileType = ImageFileType.Aseprite;
				AsepriteData = new AsepriteImageData(layerName, frameNumber, onlyVisibleLayers, includeBackgroundLayer);
				TiledData = null; // Clear other data
			}

			/// <summary>
			/// Sets Tiled-specific loading parameters
			/// </summary>
			public void SetTiledData(string imageLayerName = null)
			{
				FileType = ImageFileType.Tiled;
				TiledData = new TiledImageData(imageLayerName);
				AsepriteData = null; // Clear other data
			}

			/// <summary>
			/// Sets PNG-specific loading (no additional parameters needed)
			/// </summary>
			public void SetPngData()
			{
				FileType = ImageFileType.Png;
				AsepriteData = null; // Clear other data
				TiledData = null;
			}
		}

		private SpriteRendererComponentData _data = new SpriteRendererComponentData();

		public override ComponentData Data
		{
			get
			{
				// Always ensure _data exists
				if (_data == null)
					_data = new SpriteRendererComponentData();

				// Update current component properties
				_data.Color = Color;
				_data.LocalOffset = LocalOffset;
				_data.Origin = Origin;
				_data.LayerDepth = LayerDepth;
				_data.RenderLayer = RenderLayer;
				_data.Enabled = Enabled;
				_data.SpriteEffects = SpriteEffects;
				
				// ONLY update TextureFilePath if we don't already have one stored
				// This preserves the full path that was saved
				if (string.IsNullOrEmpty(_data.TextureFilePath) && Sprite?.Texture2D?.Name != null)
				{
					_data.TextureFilePath = Sprite.Texture2D.Name;
				}
				
				return _data;
			}
			set
			{
				if (value is SpriteRendererComponentData spriteData)
				{
					// Store the data first
					_data = spriteData;
					
					// Apply properties to component
					Color = spriteData.Color;
					LocalOffset = spriteData.LocalOffset;
					Origin = spriteData.Origin;
					LayerDepth = spriteData.LayerDepth;
					RenderLayer = spriteData.RenderLayer;
					Enabled = spriteData.Enabled;
					SpriteEffects = spriteData.SpriteEffects;
				}
			}
		}

		public override RectangleF Bounds
		{
			get
			{
				if (_areBoundsDirty)
				{
					if (_sprite != null)
						_bounds.CalculateBounds(Entity.Transform.Position, _localOffset, _origin,
							Entity.Transform.Scale, Entity.Transform.Rotation, _sprite.SourceRect.Width,
							_sprite.SourceRect.Height);
					_areBoundsDirty = false;
				}

				return _bounds;
			}
		}

		/// <summary>
		/// the origin of the Sprite. This is set automatically when setting a Sprite.
		/// </summary>
		/// <value>The origin.</value>
		public Vector2 Origin
		{
			get => _origin;
			set => SetOrigin(value);
		}

		/// <summary>
		/// helper property for setting the origin in normalized fashion (0-1 for x and y)
		/// </summary>
		/// <value>The origin normalized.</value>
		public Vector2 OriginNormalized
		{
			get => new Vector2(_origin.X / Width * Entity.Transform.Scale.X,
				_origin.Y / Height * Entity.Transform.Scale.Y);
			set => SetOrigin(new Vector2(value.X * Width / Entity.Transform.Scale.X,
				value.Y * Height / Entity.Transform.Scale.Y));
		}

		/// <summary>
		/// determines if the sprite should be rendered normally or flipped horizontally
		/// </summary>
		/// <value><c>true</c> if flip x; otherwise, <c>false</c>.</value>
		public bool FlipX
		{
			get => (SpriteEffects & SpriteEffects.FlipHorizontally) == SpriteEffects.FlipHorizontally;
			set => SpriteEffects = value
				? (SpriteEffects | SpriteEffects.FlipHorizontally)
				: (SpriteEffects & ~SpriteEffects.FlipHorizontally);
		}

		/// <summary>
		/// determines if the sprite should be rendered normally or flipped vertically
		/// </summary>
		/// <value><c>true</c> if flip y; otherwise, <c>false</c>.</value>
		public bool FlipY
		{
			get => (SpriteEffects & SpriteEffects.FlipVertically) == SpriteEffects.FlipVertically;
			set => SpriteEffects = value
				? (SpriteEffects | SpriteEffects.FlipVertically)
				: (SpriteEffects & ~SpriteEffects.FlipVertically);
		}

		/// <summary>
		/// Set the FlipX but also adjust the LocalOffset to account for the flip.  
		/// This multiplies the x value of your LocalOffset by -1 so the sprite will appear in the expected place relative to your entity 
		/// </summary>
		/// <param name="isFlippedX"></param>
		public void SetFlipXAndAdjustLocalOffset(bool isFlippedX)
		{
			if (FlipX == isFlippedX)
			{
				return;
			}

			FlipX = isFlippedX;
			LocalOffset *= new Vector2(-1, 1);
		}

		/// <summary>
		///Set the FlipY but also adjust the LocalOffset to account for the flip.  
		/// This multiplies the y value of your LocalOffset by -1 so the sprite will appear in the expected place relative to your entity 
		/// </summary>
		/// <param name="isFlippedY"></param>
		public void SetFlipYAndAdjustLocalOffset(bool isFlippedY)
		{
			if (FlipY == isFlippedY)
			{
				return;
			}

			FlipX = isFlippedY;
			LocalOffset *= new Vector2(1, -1);
		}

		/// <summary>
		/// Batchers passed along to the Batcher when rendering. flipX/flipY are helpers for setting this.
		/// </summary>
		public SpriteEffects SpriteEffects = SpriteEffects.None;

		/// <summary>
		/// the Sprite that should be displayed by this Sprite. When set, the origin of the Sprite is also set to match Sprite.origin.
		/// </summary>
		/// <value>The sprite.</value>
		public Sprite Sprite
		{
			get => _sprite;
			set => SetSprite(value);
		}

		/// <summary>
		/// The normal map sprite used for deferred lighting. Set via SetNormal().
		/// </summary>
		public Sprite NormalMap;


		protected Vector2 _origin;
		protected Sprite _sprite;


		public SpriteRenderer()
		{
		}

		public SpriteRenderer(string filePath)
		{
			_data.TextureFilePath = filePath;
		}
		public SpriteRenderer(Texture2D texture) : this(new Sprite(texture))
		{
		}

		public SpriteRenderer(Sprite sprite) => SetSprite(sprite);

		#region fluent setters

		/// <summary>
		/// sets the Sprite and updates the origin of the Sprite to match Sprite.origin. If for whatever reason you need
		/// an origin different from the Sprite either clone it or set the origin AFTER setting the Sprite here.
		/// </summary>
		public virtual SpriteRenderer SetSprite(Sprite sprite)
		{
			_sprite = sprite;
			if (_sprite != null)
				SetOrigin(_sprite.Origin); // set origin with setting _areBoundsDirty
			return this;
		}

		/// <summary>
		/// sets the Texture by creating a new sprite. See SetSprite() for details.
		/// </summary>
		public SpriteRenderer SetTexture(Texture2D texture)
		{
			SetSprite(new Sprite(texture));
			return this;
		}

		/// <summary>
		/// sets the origin for the Renderable
		/// </summary>
		public SpriteRenderer SetOrigin(Vector2 origin)
		{
			if (_origin != origin)
			{
				_origin = origin;
				_areBoundsDirty = true;
			}

			return this;
		}

		/// <summary>
		/// helper for setting the origin in normalized fashion (0-1 for x and y)
		/// </summary>
		public SpriteRenderer SetOriginNormalized(Vector2 value)
		{
			SetOrigin(new Vector2(value.X * Width / Entity.Transform.Scale.X,
				value.Y * Height / Entity.Transform.Scale.Y));
			return this;
		}

		/// <summary>
		/// Sets the normal map sprite for this renderer. Updates the material if using deferred lighting.
		/// </summary>
		/// <param name="normalMap">The normal map sprite to use.</param>
		/// <returns>This SpriteRenderer for chaining.</returns>
		public void SetNormalMap(Sprite normalMap)
		{
			NormalMap = normalMap;

			// If using a deferred material, update it with the new normal map
			if (Material != null && Material is DeferredSpriteMaterial deferredMaterial)
			{
				if(normalMap == null)
					deferredMaterial.Effect.SetNormalMap(DeferredLightingRenderer.NullNormalMapTexture);
				else
					deferredMaterial.Effect.SetNormalMap(NormalMap.Texture2D);
			}
		}


		/// <summary>
		/// Sets the normal map sprite for this renderer. Updates the material if using deferred lighting.
		/// </summary>
		/// <param name="normalMap">The normal map sprite to use.</param>
		/// <returns>This SpriteRenderer for chaining.</returns>
		public void SetNormalMap(Texture2D normalMapTexture)
		{
			if (normalMapTexture == null)
			{
				System.Console.WriteLine($"{normalMapTexture} texture is null.");
				return;
			}

			NormalMap = new Sprite(normalMapTexture);

			// If using a deferred material, update it with the new normal map
			if (Material != null && Material is DeferredSpriteMaterial deferredMaterial && normalMapTexture != null)
			{
				if (NormalMap == null)
					deferredMaterial.Effect.SetNormalMap(DeferredLightingRenderer.NullNormalMapTexture);
				else
					deferredMaterial.Effect.SetNormalMap(NormalMap.Texture2D);
			}
		}

		#endregion


		/// <summary>
		/// Draws the Renderable with an outline. Note that this should be called on disabled Renderables since they shouldn't take part in default
		/// rendering if they need an outline.
		/// </summary>
		public void DrawOutline(Batcher batcher, Camera camera, int offset = 1) =>
			DrawOutline(batcher, camera, Color.Black, offset);

		public void DrawOutline(Batcher batcher, Camera camera, Color outlineColor, int offset = 1)
		{
			// save the stuff we are going to modify so we can restore it later
			var originalPosition = _localOffset;
			var originalColor = Color;
			var originalLayerDepth = _layerDepth;

			// set our new values
			Color = outlineColor;
			_layerDepth += 0.01f;

			for (var i = -1; i < 2; i++)
			{
				for (var j = -1; j < 2; j++)
				{
					if (i != 0 || j != 0)
					{
						_localOffset = originalPosition + new Vector2(i * offset, j * offset);
						Render(batcher, camera);
					}
				}
			}

			// restore changed state
			_localOffset = originalPosition;
			Color = originalColor;
			_layerDepth = originalLayerDepth;
		}

		public override void Render(Batcher batcher, Camera camera)
		{
			batcher.Draw(Sprite, Entity.Transform.Position + LocalOffset, Color, Entity.Transform.Rotation, 
						 Origin, Entity.Transform.Scale, SpriteEffects, LayerDepth);
		}

		/// <summary>
		/// Called when this component is added to an entity. 
		/// If we have saved texture file path data, load the image automatically.
		/// </summary>
		public override void OnAddedToEntity()
		{
			base.OnAddedToEntity();

			// If we have texture file path data but no sprite loaded, load it
			if (!string.IsNullOrEmpty(_data?.TextureFilePath) && Sprite == null)
			{
				LoadImageFromData();
			}

			// Set up deferred material 
			if (Entity.Scene.GetRenderer<DeferredLightingRenderer>() != null)
			{
				if (!string.IsNullOrEmpty(_data?.NormalMapFilePath)) //Has normal map
				{
					SetNormalMap(LoadNormalMap(_data.NormalMapFilePath, _data.NormalMapFileType));
					SetMaterial(new DeferredSpriteMaterial(NormalMap));
				}
				// else // doesn't have
				// 	SetMaterial(new DeferredSpriteMaterial(Entity.Scene.GetRenderer<DeferredLightingRenderer>().NullNormalMapTexture));
			}
		}

		/// <summary>
		/// Loads an image based on the ComponentData settings
		/// </summary>
		public void LoadImageFromData()
		{
			// Check if we have data to load
			if (string.IsNullOrEmpty(_data.TextureFilePath))
			{
				Debug.Warn($"SpriteRenderer has no texture file path to load from.");
				return;
			}

			// Try to get content manager, but handle case where entity isn't in scene yet
			var contentManager = Entity?.Scene?.Content ?? Core.Content;
			if (contentManager == null)
			{
				Debug.Warn($"No content manager available to load image file: {_data.TextureFilePath}. Will retry when entity is added to scene.");
				return;
			}

			// Store the saved origin BEFORE loading the sprite
			var savedOrigin = _data.Origin;

			switch (_data.FileType)
			{
				case SpriteRendererComponentData.ImageFileType.Png:
					LoadPngFile(_data.TextureFilePath);
					break;

				case SpriteRendererComponentData.ImageFileType.Aseprite:
					if (_data.AsepriteData.HasValue)
					{
						var aseData = _data.AsepriteData.Value;
						LoadAsepriteFile(_data.TextureFilePath, aseData.LayerName, aseData.FrameNumber);
					}
					else
					{
						LoadAsepriteFile(_data.TextureFilePath);
					}
					break;

				case SpriteRendererComponentData.ImageFileType.Tiled:
					if (_data.TiledData.HasValue)
					{
						var tiledData = _data.TiledData.Value;
						LoadTmxFile(_data.TextureFilePath, tiledData.ImageLayerName);
					}
					else
					{
						LoadTmxFile(_data.TextureFilePath);
					}
					break;

				case SpriteRendererComponentData.ImageFileType.None:
					// No file type specified, try to determine from extension
					if (!string.IsNullOrEmpty(_data.TextureFilePath))
					{
						var extension = Path.GetExtension(_data.TextureFilePath).ToLower();
						switch (extension)
						{
							case ".png": 
								LoadPngFile(_data.TextureFilePath);
								break;
							case ".ase":
							case ".aseprite":
								LoadAsepriteFile(_data.TextureFilePath);
								break;
							case ".tmx":
								LoadTmxFile(_data.TextureFilePath);
								break;
							default:
								Debug.Error($"Unknown file extension for texture: {_data.TextureFilePath}");
								break;
						}
					}
					break;

				default:
					Debug.Error($"Unknown or unsupported file type for: {_data.TextureFilePath}");
					break;
			}

			// This ensures manually set origins are preserved
			if (savedOrigin != Vector2.Zero || _data.Origin != Vector2.Zero)
			{
				SetOrigin(savedOrigin);
			}

			// After loading the main image, load the normal map if specified
			if (!string.IsNullOrEmpty(_data.NormalMapFilePath))
			{
				contentManager = Entity?.Scene?.Content ?? Core.Content;

				switch (_data.NormalMapFileType)
				{
					case SpriteRendererComponentData.ImageFileType.Png:
						SetNormalMap(contentManager.LoadTexture(_data.NormalMapFilePath));
						break;
					case SpriteRendererComponentData.ImageFileType.Aseprite:
						// For Aseprite, you may want to load a specific layer or frame as the normal map
						var aseFile = contentManager.LoadAsepriteFile(_data.NormalMapFilePath);
						if (aseFile != null)
						{
							var aseData = _data.AsepriteData.Value;
							// Use the first frame/layer as normal map by default
							SetNormalMap(aseFile.GetTextureFromFrameNumber(aseData.FrameNumber + 1)); //TODO: fix frame number handling
						}
						break;
					case SpriteRendererComponentData.ImageFileType.Tiled:
						var tiledMap = contentManager.LoadTiledMap(_data.NormalMapFilePath);
						if (tiledMap.ImageLayers.Count > 0)
							SetNormalMap(tiledMap.ImageLayers[0].Image.Texture);
						break;
					case SpriteRendererComponentData.ImageFileType.None:
					default:
						// Try to infer from extension
						var ext = Path.GetExtension(_data.NormalMapFilePath).ToLower();
						if (ext == ".png")
							SetNormalMap(contentManager.LoadTexture(_data.NormalMapFilePath));
						break;
				}
			}
		}

		/// <summary>
		/// Loads a PNG/JPG file and creates a sprite from it
		/// </summary>
		/// <param name="filepath">The file path relative to the project root (including Content/ prefix)</param>
		/// <param name="contentManager">The content manager to use for loading</param>
		/// <returns>The SpriteRenderer for method chaining</returns>
		public SpriteRenderer LoadPngFile(string filepath)
		{
			var contentManager = Entity?.Scene?.Content ?? Core.Content;
			if (contentManager == null)
			{
				throw new Exception($"No content manager available to load image file: {_data.TextureFilePath}. Will retry when entity is added to scene.");
			}

			try
			{
				// Ensure the path is properly formatted for NezContentManager
				var normalizedPath = filepath.Replace('\\', '/');
				
				var texture = contentManager.LoadTexture(normalizedPath);
				if (texture != null)
				{
					SetSprite(new Sprite(texture));
					
					// Update the data field properly
					_data.SetPngData();
					_data.TextureFilePath = normalizedPath;
				}
				else
				{
					Debug.Error($"Failed to load PNG file: {normalizedPath}");
				}
			}
			catch (Exception e)
			{
				Debug.Error($"Error loading PNG file {filepath}: {e.Message}");
			}

			return this;
		}

		/// <summary>
		/// Loads an Aseprite file and creates a sprite from a specific frame and layer(s)
		/// </summary>
		/// <param name="filepath">The file path relative to the Content directory</param>
		/// <param name="contentManager">The content manager to use for loading</param>
		/// <param name="layerName">Optional specific layer name to include. If null, all visible layers will be included</param>
		/// <param name="frameNumber">The frame number to load (0-based index). Defaults to 0</param>
		/// <returns>The SpriteRenderer for method chaining</returns>
		public SpriteRenderer LoadAsepriteFile(string filepath, string layerName = null, int frameNumber = 0)
		{
			var contentManager = Entity?.Scene?.Content ?? Core.Content;
			if (contentManager == null)
			{
				throw new Exception($"No content manager available to load image file: {_data.TextureFilePath}. Will retry when entity is added to scene.");
			}

			try
			{
				var asepriteFile = contentManager.LoadAsepriteFile(filepath);
				if (asepriteFile != null)
				{
					// Validate frame number
					if (frameNumber < 0 || frameNumber >= asepriteFile.Frames.Count)
					{
						Debug.Error($"Frame number {frameNumber} is out of range. File has {asepriteFile.Frames.Count} frames. Using frame 0 instead.");
						frameNumber = 0;
					}

					// Use AsepriteUtils to load the specific frame with layer filtering
					Sprite sprite;
					if (!string.IsNullOrEmpty(layerName))
					{
						sprite = AsepriteUtils.LoadAsepriteFrameFromLayer(filepath, frameNumber, layerName);
					}
					else
					{
						sprite = AsepriteUtils.LoadAsepriteFrame(filepath, frameNumber);
					}

					if (sprite != null)
					{
						SetSprite(sprite);
						
						// Update the data field properly
						_data.SetAsepriteData(layerName, frameNumber);
						_data.TextureFilePath = filepath;
					}
					else
					{
						Debug.Error($"Failed to create sprite from Aseprite file: {filepath}");
					}
				}
				else
				{
					Debug.Error($"Failed to load Aseprite file: {filepath}");
				}
			}
			catch (Exception e)
			{
				Debug.Error($"Error loading Aseprite file {filepath}: {e.Message}");
			}

			return this;
		}

		/// <summary>
		/// Loads a TMX (Tiled map) file and creates a sprite from its texture
		/// </summary>
		/// <param name="filepath">The file path relative to the Content directory</param>
		/// <param name="contentManager">The content manager to use for loading</param>
		/// <returns>The SpriteRenderer for method chaining</returns>
		public SpriteRenderer LoadTmxFile(string filepath, string imageLayerName = null)
		{
			var contentManager = Entity?.Scene?.Content ?? Core.Content;
			if (contentManager == null)
			{
				throw new Exception($"No content manager available to load image file: {_data.TextureFilePath}. Will retry when entity is added to scene.");
			}

			try
			{
				var tiledMap = contentManager.LoadTiledMap(filepath);

				if (imageLayerName != null)
				{
					foreach (var image in tiledMap.ImageLayers)
					{
						if (image.Name == imageLayerName)
						{
							SetSprite(new Sprite(image.Image.Texture));
							
							// Update the data field properly
							_data.SetTiledData(imageLayerName);
							_data.TextureFilePath = filepath;
							
							return this;
						}
					}
					Debug.Error($"Image layer '{imageLayerName}' not found in TMX file: {filepath}");
				}
				else
				{
					if (tiledMap.ImageLayers.Count > 0 && tiledMap.ImageLayers[0].Image.Texture != null)
					{
						SetSprite(new Sprite(tiledMap.ImageLayers[0].Image.Texture));
						
						// Update the data field properly
						_data.SetTiledData();
						_data.TextureFilePath = filepath;
					}
					else
					{
						Debug.Error("Error: There was no image layer in the TMX file: " + filepath);
					}
				}
			}
			catch (Exception e)
			{
				Debug.Error($"Error loading TMX file {filepath}: {e.Message}");
			}

			return this;
		}

		/// <summary>
		/// Creates a deep clone of this SpriteRenderer component.
		/// </summary>
		/// <returns>A new SpriteRenderer instance with all properties and data deep-copied</returns>
		public override Component Clone()
		{
			var clone = new SpriteRenderer();
			
			// Copy basic RenderableComponent properties
			clone.LocalOffset = LocalOffset;
			clone.Color = Color;
			clone.LayerDepth = LayerDepth;
			clone.RenderLayer = RenderLayer;
			clone.Enabled = Enabled;
			clone.Name = Name;
			
			// Deep clone Material to prevent shared references
			if (Material != null)
			{
				clone.SetMaterial(Material.Clone()); 
			}
			else
				clone.SetMaterial(null); 


			// Copy SpriteRenderer-specific properties
			clone.SpriteEffects = SpriteEffects;
			
			// Deep clone the sprite if it exists
			if (_sprite != null)
			{
				clone._sprite = _sprite.Clone();
			}
			
			// Deep clone the origin
			clone._origin = _origin;
			
			// Deep clone the component data
			if (_data != null)
			{
				clone._data = CloneSpriteRendererComponentData(_data);
			}
			else
			{
				clone._data = new SpriteRendererComponentData();
			}
			
			// Reset entity-specific state (the clone isn't attached to any entity yet)
			clone.Entity = null;
			clone._areBoundsDirty = true;
			
			return clone;
		}

		/// <summary>
		/// Helper method to deep clone SpriteRendererComponentData
		/// </summary>
		/// <param name="original">The original data to clone</param>
		/// <returns>A deep copy of the component data</returns>
		private static SpriteRendererComponentData CloneSpriteRendererComponentData(SpriteRendererComponentData original)
		{
			var clone = new SpriteRendererComponentData();
			
			// Copy all primitive properties
			clone.TextureFilePath = original.TextureFilePath;
			clone.ColorR = original.ColorR;
			clone.ColorG = original.ColorG;
			clone.ColorB = original.ColorB;
			clone.ColorA = original.ColorA;
			clone.LocalOffset = original.LocalOffset;
			clone.Origin = original.Origin;
			clone.LayerDepth = original.LayerDepth;
			clone.RenderLayer = original.RenderLayer;
			clone.Enabled = original.Enabled;
			clone.SpriteEffects = original.SpriteEffects;
			clone.FileType = original.FileType;
			
			// Deep clone the nullable structs
			if (original.AsepriteData.HasValue)
			{
				var originalAse = original.AsepriteData.Value;
				clone.AsepriteData = new SpriteRendererComponentData.AsepriteImageData(
					originalAse.LayerName,
					originalAse.FrameNumber,
					originalAse.OnlyVisibleLayers,
					originalAse.IncludeBackgroundLayer
				);
			}
			else
			{
				clone.AsepriteData = null;
			}
			
			if (original.TiledData.HasValue)
			{
				var originalTiled = original.TiledData.Value;
				clone.TiledData = new SpriteRendererComponentData.TiledImageData(
					originalTiled.ImageLayerName
				);
			}
			else
			{
				clone.TiledData = null;
			}
			
			return clone;
		}
		
		public Texture2D LoadNormalMap(string normalMapFilePath, SpriteRendererComponentData.ImageFileType fileType)
		{
			_data.NormalMapFilePath = normalMapFilePath;
			_data.NormalMapFileType = fileType;
			
			// Remove normal map if path is empty
			if (string.IsNullOrEmpty(_data.NormalMapFilePath))
			{
				SetMaterial(null);
				return null;
			}

			var contentManager = Entity?.Scene?.Content ?? Core.Content;
			Texture2D normalMapTexture = null;

			try
			{
				switch (fileType)
				{
					case SpriteRendererComponentData.ImageFileType.Png:
						normalMapTexture = contentManager.LoadTexture(_data.NormalMapFilePath);
						break;
					case SpriteRendererComponentData.ImageFileType.Aseprite:
						var aseFile = contentManager.LoadAsepriteFile(_data.NormalMapFilePath);
						if (aseFile != null)
						{
							// Use first frame by default, or extend as needed
							normalMapTexture = aseFile.GetTextureFromFrameNumber(1); //frame zero
						}
						break;
					case SpriteRendererComponentData.ImageFileType.Tiled:
						var tiledMap = contentManager.LoadTiledMap(_data.NormalMapFilePath);
						if (tiledMap.ImageLayers.Count > 0)
							normalMapTexture = tiledMap.ImageLayers[0].Image.Texture;
						break;
					default:
						// Try to infer from extension
						var ext = System.IO.Path.GetExtension(_data.NormalMapFilePath).ToLower();
						if (ext == ".png")
							normalMapTexture = contentManager.LoadTexture(_data.NormalMapFilePath);
						break;
				}
			}
			catch (Exception e)
			{
				Debug.Error($"Error loading normal map: {e.Message}");
			}

			return normalMapTexture;
		}
	}
}