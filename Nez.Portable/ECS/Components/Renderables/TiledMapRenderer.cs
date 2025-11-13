using System;
using Microsoft.Xna.Framework;
using Nez.Systems;
using Nez.Tiled;
using Nez.Utils.Extensions;
using System.Collections.Generic;
using System.IO;
using static System.Runtime.InteropServices.JavaScript.JSType;


namespace Nez
{
	public class TiledMapRenderer : RenderableComponent, IUpdatable
	{
		public TmxMap TiledMap;

		public int PhysicsLayer = 1 << 0;

		/// <summary>
		/// if null, all layers will be rendered
		/// </summary>
		public int[] LayerIndicesToRender;

		public bool AutoUpdateTilesets = true;

		public override float Width => TiledMap.Width * TiledMap.TileWidth;
		public override float Height => TiledMap.Height * TiledMap.TileHeight;

		public TmxLayer CollisionLayer;

		bool _shouldCreateColliders;
		Collider[] _colliders;


		public TiledMapRenderer(TmxMap tiledMap, string collisionLayerName = null, bool shouldCreateColliders = true)
		{
			TiledMap = tiledMap;
			_shouldCreateColliders = shouldCreateColliders;

			if (collisionLayerName != null && tiledMap != null)
				CollisionLayer = tiledMap.TileLayers[collisionLayerName];
		}

		public TiledMapRenderer() : base()
		{
		}

		public TiledMapRenderer(string tiledMapPath) : base()
		{
			if (_data == null)
				_data = new TiledMapRendererComponentData();
				
			_data.TiledMapPath = tiledMapPath;
		}

		private TiledMapRendererComponentData _data = new TiledMapRendererComponentData();

		public class TiledMapRendererComponentData : ComponentData
		{
			public string TiledMapPath;
			public int PhysicsLayer = 1 << 0;
			public int[] LayerIndicesToRender;
			public bool AutoUpdateTilesets = true;
			public string CollisionLayerName;
			public bool ShouldCreateColliders = true;
			public float LayerDepth;
			public int RenderLayer;
			public Vector2 LocalOffset;
			public Color Color = Color.White;
		}

		public override ComponentData Data
		{
			get
			{
				if (_data == null)
					_data = new TiledMapRendererComponentData();

				_data.Enabled = Enabled;
				_data.PhysicsLayer = PhysicsLayer;
				_data.LayerIndicesToRender = LayerIndicesToRender;
				_data.AutoUpdateTilesets = AutoUpdateTilesets;
				_data.CollisionLayerName = CollisionLayer?.Name;
				_data.ShouldCreateColliders = _shouldCreateColliders;
				_data.LayerDepth = LayerDepth;
				_data.RenderLayer = RenderLayer;
				_data.LocalOffset = LocalOffset;
				_data.Color = Color;
				
				// Preserve TiledMapPath if it exists
				if (string.IsNullOrEmpty(_data.TiledMapPath) && TiledMap != null)
				{
					_data.TiledMapPath = TiledMap.TmxDirectory;
				}
				
				return _data;
			}
			set
			{
				if (value is TiledMapRendererComponentData data)
				{
					_data = data;
					
					Enabled = data.Enabled;
					PhysicsLayer = data.PhysicsLayer;
					LayerIndicesToRender = data.LayerIndicesToRender;
					AutoUpdateTilesets = data.AutoUpdateTilesets;
					_shouldCreateColliders = data.ShouldCreateColliders;
					LayerDepth = data.LayerDepth;
					RenderLayer = data.RenderLayer;
					LocalOffset = data.LocalOffset;
					Color = data.Color;
				}
			}
		}

		public void SetLayerToRender(string layerName)
		{
			LayerIndicesToRender = new int[1];
			LayerIndicesToRender[0] = TiledMap.Layers.IndexOf(TiledMap.GetLayer(layerName));
		}

		/// <summary>
		/// sets which layers should be rendered by this component by name. If you know the indices you can set layerIndicesToRender directly.
		/// </summary>
		/// <param name="layerNames">Layer names.</param>
		public void SetLayersToRender(params string[] layerNames)
		{
			LayerIndicesToRender = new int[layerNames.Length];

			for (var i = 0; i < layerNames.Length; i++)
				LayerIndicesToRender[i] = TiledMap.Layers.IndexOf(TiledMap.GetLayer(layerNames[i]));
		}


		#region TiledMap queries

		public int GetRowAtWorldPosition(float yPos)
		{
			yPos -= Entity.Transform.Position.Y + _localOffset.Y;
			return TiledMap.WorldToTilePositionY(yPos);
		}

		public int GetColumnAtWorldPosition(float xPos)
		{
			xPos -= Entity.Transform.Position.X + _localOffset.X;
			return TiledMap.WorldToTilePositionX(xPos);
		}

		/// <summary>
		/// this method requires that you are using a collision layer setup in the constructor.
		/// </summary>
		public TmxLayerTile GetTileAtWorldPosition(Vector2 worldPos)
		{
			Insist.IsNotNull(CollisionLayer, "collisionLayer must not be null!");

			// offset the passed in world position to compensate for the entity position
			worldPos -= Entity.Transform.Position + _localOffset;

			return CollisionLayer.GetTileAtWorldPosition(worldPos);
		}

		/// <summary>
		/// gets all the non-empty tiles that intersect the passed in bounds for the collision layer. The returned List can be put back in the
		/// pool via ListPool.free.
		/// </summary>
		/// <returns>The tiles intersecting bounds.</returns>
		/// <param name="bounds">Bounds.</param>
		public List<TmxLayerTile> GetTilesIntersectingBounds(Rectangle bounds)
		{
			Insist.IsNotNull(CollisionLayer, "collisionLayer must not be null!");

			// offset the passed in world position to compensate for the entity position
			bounds.Location -= (Entity.Transform.Position + _localOffset).ToPoint();
			return CollisionLayer.GetTilesIntersectingBounds(bounds);
		}

		#endregion


		#region Component overrides

		public override void OnEntityTransformChanged(Transform.Component comp)
		{
			// we only deal with positional changes here. TiledMaps cant be scaled.
			if (_shouldCreateColliders && comp == Transform.Component.Position)
			{
				RemoveColliders();
				AddColliders();
			}
		}

		public override void OnAddedToEntity()
		{
			base.OnAddedToEntity();
			
			// Auto-load TiledMap if we have a path but no map loaded
			if (!string.IsNullOrEmpty(_data?.TiledMapPath) && TiledMap == null)
			{
				LoadTiledMapFromData();
			}
			
			AddColliders();
		}

		public override void OnRemovedFromEntity() => RemoveColliders();

		public virtual void Update()
		{
			if(TiledMap == null)
				return;

			if (AutoUpdateTilesets)
				TiledMap.Update();
		}

		public override void Render(Batcher batcher, Camera camera)
		{
			if (LayerIndicesToRender == null)
			{
				TiledRendering.RenderMap(TiledMap, batcher, Entity.Transform.Position + _localOffset, Transform.Scale, LayerDepth, camera.Bounds);
			}
			else
			{
				for (var i = 0; i < TiledMap.Layers.Count; i++)
				{
					if (TiledMap.Layers[i].Visible && LayerIndicesToRender.Contains(i))
						TiledRendering.RenderLayer(TiledMap.Layers[i], batcher, Entity.Transform.Position + _localOffset, Transform.Scale, LayerDepth, camera.Bounds);
				}
			}
		}

		public override void DebugRender(Batcher batcher)
		{
			foreach (var group in TiledMap.ObjectGroups)
				TiledRendering.RenderObjectGroup(group, batcher, Entity.Transform.Position + _localOffset, Transform.Scale, LayerDepth);

			if (_colliders != null)
			{
				foreach (var collider in _colliders)
					collider.DebugRender(batcher);
			}
		}

		#endregion


		#region Colliders

		public void AddColliders()
		{
			if (CollisionLayer == null || !_shouldCreateColliders)
				return;

			var collisionRects = CollisionLayer.GetCollisionRectangles();

			// create colliders for the rects we received
			_colliders = new Collider[collisionRects.Count];
			for (var i = 0; i < collisionRects.Count; i++)
			{
				var collider = new BoxCollider(collisionRects[i].X + _localOffset.X,
					collisionRects[i].Y + _localOffset.Y, collisionRects[i].Width, collisionRects[i].Height);
				collider.PhysicsLayer = PhysicsLayer;
				collider.Entity = Entity;
				_colliders[i] = collider;

				Physics.AddCollider(collider);
			}
		}

		public void RemoveColliders()
		{
			if (_colliders == null)
				return;

			foreach (var collider in _colliders)
				Physics.RemoveCollider(collider);
			_colliders = null;
		}

		#endregion

		/// <summary>
		/// Loads the TiledMap based on the stored ComponentData settings
		/// </summary>
		public void LoadTiledMapFromData()
		{
			if (string.IsNullOrEmpty(_data?.TiledMapPath))
			{
				Debug.Log(Debug.LogType.Warn, "TiledMapRenderer has no TiledMapPath to load from.");
				return;
			}

			var contentManager = Entity?.Scene?.Content ?? Core.Content;
			if (contentManager == null)
			{
				Debug.Log(Debug.LogType.Warn, $"No content manager available to load TMX file: {_data.TiledMapPath}");
				return;
			}

			try
			{
				TiledMap = contentManager.LoadTiledMap(_data.TiledMapPath);

				// Set collision layer if specified
				if (!string.IsNullOrEmpty(_data.CollisionLayerName) && 
				    TiledMap.TileLayers.Contains(_data.CollisionLayerName))
				{
					CollisionLayer = TiledMap.TileLayers[_data.CollisionLayerName];
				}

				Debug.Log($"Successfully loaded TiledMap from: {_data.TiledMapPath}");
			}
			catch (Exception ex)
			{
				Debug.Log(Debug.LogType.Error, $"Failed to load TiledMap from {_data.TiledMapPath}: {ex.Message}");
			}
		}
	}
}
