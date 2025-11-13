using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Nez.PhysicsShapes;


namespace Nez
{
	public abstract class Collider : Component
	{
		#region Component Data
		public class ColliderComponentData : ComponentData
		{
			// Common to all colliders
			public bool IsTrigger;
			public int PhysicsLayer;
			public int CollidesWithLayers;
			public bool ShouldColliderScaleAndRotateWithTransform;
			public bool IsVisibleEvenDisabled;
			public bool DebugEnabled = true;
			public Vector2 LocalOffset;

			// BoxCollider
			public RectangleF Rectangle;

			// CircleCollider
			public float CircleRadius;
			public Vector2 CircleOffset;

			// PolygonCollider
			public List<Vector2> PolygonPoints; 
		}

		public override ComponentData Data
		{
			get
			{
				// Let PolygonCollider handle its own serialization
				if (this is PolygonCollider)
				{
					return (this as PolygonCollider).Data;
				}

				_data.Enabled = Enabled;
				_data.IsTrigger = IsTrigger;
				_data.PhysicsLayer = PhysicsLayer;
				_data.CollidesWithLayers = CollidesWithLayers;
				_data.ShouldColliderScaleAndRotateWithTransform = ShouldColliderScaleAndRotateWithTransform;
				_data.IsVisibleEvenDisabled = IsVisibleEvenDisabled;
				_data.DebugEnabled = DebugRenderEnabled;
				_data.LocalOffset = LocalOffset;

				if (this is BoxCollider box)
				{
					var width = box.Width;
					var height = box.Height;
					var offset = box.LocalOffset;
					_data.Rectangle = new RectangleF(offset.X - width / 2f, offset.Y - height / 2f, width, height);
				}
				else if (this is CircleCollider circle)
				{
					_data.CircleRadius = circle.Radius;
					_data.CircleOffset = circle.LocalOffset;
				}

				return _data;
			}
			set
			{
				// Let PolygonCollider handle its own deserialization
				if (this is PolygonCollider polyCollider && value is ColliderComponentData)
				{
					polyCollider.Data = value;
					return;
				}

				if (value is ColliderComponentData colliderData)
				{
					Enabled = colliderData.Enabled;
					IsTrigger = colliderData.IsTrigger;
					PhysicsLayer = colliderData.PhysicsLayer;
					CollidesWithLayers = colliderData.CollidesWithLayers;
					ShouldColliderScaleAndRotateWithTransform = colliderData.ShouldColliderScaleAndRotateWithTransform;
					IsVisibleEvenDisabled = colliderData.IsVisibleEvenDisabled;
					DebugRenderEnabled = colliderData.DebugEnabled;
					LocalOffset = colliderData.LocalOffset;

					if (this is BoxCollider box)
					{
						box.LocalOffset = new Vector2(
							colliderData.Rectangle.X + colliderData.Rectangle.Width / 2f,
							colliderData.Rectangle.Y + colliderData.Rectangle.Height / 2f
						);
						box.SetSize(colliderData.Rectangle.Width, colliderData.Rectangle.Height);
					}
					else if (this is CircleCollider circle)
					{
						circle.LocalOffset = colliderData.CircleOffset;
						circle.Radius = colliderData.CircleRadius;
					}

					_data = colliderData;
				}
			}
		}

		private ColliderComponentData _data = new ColliderComponentData();
		/// <summary>
		/// the underlying Shape of the Collider
		/// </summary>
		public Shape Shape;

		/// <summary>
		/// localOffset is added to entity.position to get the final position for the collider geometry. This allows you to add multiple
		/// Colliders to an Entity and position them separately and also lets you set the point of rotation/scale.
		/// </summary>
		public Vector2 LocalOffset
		{
			get => _localOffset;
			set => SetLocalOffset(value);
		}

		/// <summary>
		/// represents the absolute position to this Collider. It is entity.transform.position + localPosition - origin.
		/// </summary>
		/// <value>The absolute position.</value>
		public Vector2 AbsolutePosition => Entity.Transform.Position + _localOffset;

		/// <summary>
		/// wraps Transform.rotation and returns 0 if this Collider does not rotate with the Entity else it returns Transform.rotation
		/// </summary>
		/// <value>The rotation.</value>
		public float Rotation
		{
			get
			{
				if (ShouldColliderScaleAndRotateWithTransform && Entity?.Transform != null)
					return Entity.Transform.Rotation;

				return 0;
			}
		}

		/// <summary>
		/// if this collider is a trigger it will not cause collisions but it will still trigger events
		/// </summary>
		public bool IsTrigger;

		/// <summary>
		/// physicsLayer can be used as a filter when dealing with collisions. The Flags class has methods to assist with bitmasks.
		/// </summary>
		public int PhysicsLayer = 1 << 0;

		/// <summary>
		/// layer mask of all the layers this Collider should collide with when Entity.move methods are used. defaults to all layers.
		/// </summary>
		public int CollidesWithLayers = Physics.AllLayers;

		/// <summary>
		/// if true, the Collider will scale and rotate following the Transform it is attached to
		/// </summary>
		public bool ShouldColliderScaleAndRotateWithTransform = true;

		public bool IsVisibleEvenDisabled;
		public bool DebugRenderEnabled;

		public virtual RectangleF Bounds
		{
			get
			{
				if (_isPositionDirty || _isRotationDirty)
				{
					Shape.RecalculateBounds(this);
					_isPositionDirty = _isRotationDirty = false;
				}

				return Shape.Bounds;
			}
		}

		/// <summary>
		/// the bounds of this Collider when it was registered with the Physics system. Storing this allows us to always be able to
		/// safely remove the Collider from the Physics system even if it was moved before attempting to remove it.
		/// </summary>
		internal RectangleF registeredPhysicsBounds;

		#endregion
		protected Vector2 _localOffset;
		internal float _localOffsetLength;
		protected bool _colliderRequiresAutoSizing;

		/// <summary>
		/// flag to keep track of if our Entity was added to a Scene
		/// </summary>
		protected bool _isParentEntityAddedToScene;

		/// <summary>
		/// flag to keep track of if we registered ourself with the Physics system
		/// </summary>
		protected bool _isColliderRegistered;

		internal bool _isPositionDirty = true;
		internal bool _isRotationDirty = true;


		#region Fluent setters

		/// <summary>
		/// localOffset is added to entity.position to get the final position for the collider. This allows you to add multiple Colliders
		/// to an Entity and position them separately.
		/// </summary>
		/// <returns>The local offset.</returns>
		/// <param name="offset">Offset.</param>
		public Collider SetLocalOffset(Vector2 offset)
		{
			if (_localOffset != offset)
			{
				// Add null safety check
				if (Enabled && Entity != null)
					UnregisterColliderWithPhysicsSystem();
				
				_localOffset = offset;
				_localOffsetLength = _localOffset.Length();
				_isPositionDirty = true;
				
				// Add null safety check
				if (Enabled && Entity != null)
					RegisterColliderWithPhysicsSystem();
			}

			return this;
		}


		/// <summary>
		/// if set to true, the Collider will scale and rotate following the Transform it is attached to
		/// </summary>
		/// <returns>The should collider scale and rotate with transform.</returns>
		/// <param name="shouldColliderScaleAndRotateWithTransform">If set to <c>true</c> should collider scale and rotate with transform.</param>
		public Collider SetShouldColliderScaleAndRotateWithTransform(bool shouldColliderScaleAndRotateWithTransform)
		{
			ShouldColliderScaleAndRotateWithTransform = shouldColliderScaleAndRotateWithTransform;
			_isPositionDirty = _isRotationDirty = true;
			return this;
		}

		/// <summary>
		/// Remove the specified layer from the CollidesWithLayers bitmask
		/// </summary>
		/// <param name="layerToIgnore"></param>
		public void IgnoreLayer(int layerToIgnore)
		{
			CollidesWithLayers &= ~layerToIgnore;
		}

		/// <summary>
		/// Adds the specified layer to the CollidesWithLayers bitmask
		/// </summary>
		/// <param name="layerToAdd"></param>
		public void AddLayer(int layerToAdd)
		{
			CollidesWithLayers |= layerToAdd;
		}

		#endregion


		#region Component Lifecycle

		public override void OnAddedToEntity()
		{
			if (_colliderRequiresAutoSizing)
			{
				// we only deal with boxes and circles here
				Insist.IsTrue(this is BoxCollider || this is CircleCollider,
					"Only box and circle colliders can be created automatically");

				var renderable = Entity.GetComponent<RenderableComponent>();
				Debug.WarnIf(renderable == null,
					"Collider has no shape and no RenderableComponent. Can't figure out how to size it.");
				if (renderable != null)
				{
					var renderableBounds = renderable.Bounds;

					// we need the size * inverse scale here because when we autosize the Collider it needs to be without a scaled Renderable
					var width = renderableBounds.Width / Entity.Transform.Scale.X;
					var height = renderableBounds.Height / Entity.Transform.Scale.Y;

					// circle colliders need special care with the origin
					if (this is CircleCollider)
					{
						var circleCollider = this as CircleCollider;
						circleCollider.Radius = Math.Max(width, height) * 0.5f;

						// fetch the Renderable's center, transfer it to local coordinates and use that as the localOffset of our collider
						LocalOffset = renderableBounds.Center - Entity.Transform.Position;
					}
					else
					{
						var boxCollider = this as BoxCollider;
						boxCollider.Width = width;
						boxCollider.Height = height;

						// fetch the Renderable's center, transfer it to local coordinates and use that as the localOffset of our collider
						LocalOffset = renderableBounds.Center - Entity.Transform.Position;
					}
				}
			}

			_isParentEntityAddedToScene = true;
			if(Enabled)
				RegisterColliderWithPhysicsSystem();
		}


		public override void OnRemovedFromEntity()
		{
			UnregisterColliderWithPhysicsSystem();
			_isParentEntityAddedToScene = false;
		}


		public override void OnEntityTransformChanged(Transform.Component comp)
		{
			// Always update position/rotation/scale if visible, even when disabled
			if (Enabled || IsVisibleEvenDisabled)
			{
				switch (comp)
				{
					case Transform.Component.Position:
						_isPositionDirty = true;
						break;
					case Transform.Component.Scale:
						_isPositionDirty = true;
						break;
					case Transform.Component.Rotation:
						_isRotationDirty = true;
						break;
				}

				// Only update physics if enabled
				if (_isColliderRegistered && Enabled)
					Physics.UpdateCollider(this);
			}
		}


		public override void OnEnabled()
		{
			RegisterColliderWithPhysicsSystem();
			_isPositionDirty = _isRotationDirty = true;
		}


		public override void OnDisabled()
		{
			UnregisterColliderWithPhysicsSystem();
		}

		#endregion


		/// <summary>
		/// the parent Entity will call this at various times (when added to a scene, enabled, etc)
		/// </summary>
		public virtual void RegisterColliderWithPhysicsSystem()
		{
			// entity could be null if properties such as origin are changed before we are added to an Entity
			if (_isParentEntityAddedToScene && !_isColliderRegistered && Enabled)
			{
				Physics.AddCollider(this);
				_isColliderRegistered = true;
			}
		}


		/// <summary>
		/// the parent Entity will call this at various times (when removed from a scene, disabled, etc)
		/// </summary>
		public virtual void UnregisterColliderWithPhysicsSystem()
		{
			if (_isParentEntityAddedToScene && _isColliderRegistered)
				Physics.RemoveCollider(this);
			_isColliderRegistered = false;
		}


		#region collision checks

		/// <summary>
		/// checks to see if this shape overlaps any other Colliders in the Physics system
		/// </summary>
		/// <param name="collider">Collider.</param>
		public bool Overlaps(Collider other)
		{
			if (!Enabled) return false;
			return Shape.Overlaps(other.Shape);
		}


		/// <summary>
		/// checks to see if this Collider collides with collider. If it does, true will be returned and result will be populated
		/// with collision data
		/// </summary>
		/// <returns><c>true</c>, if with was collidesed, <c>false</c> otherwise.</returns>
		/// <param name="collider">Collider.</param>
		/// <param name="result">Result.</param>
		public bool CollidesWith(Collider collider, out CollisionResult result)
		{
			result = default;
			if (!Enabled) return false;
			if (Shape.CollidesWithShape(collider.Shape, out result))
			{
				result.Collider = collider;
				return true;
			}
			return false;
		}


		/// <summary>
		/// checks to see if this Collider with motion applied (delta movement vector) collides with collider. If it does, true will be
		/// returned and result will be populated with collision data.
		/// </summary>
		/// <returns><c>true</c>, if with was collidesed, <c>false</c> otherwise.</returns>
		/// <param name="collider">Collider.</param>
		/// <param name="motion">Motion.</param>
		/// <param name="result">Result.</param>
		public bool CollidesWith(Collider collider, Vector2 motion, out CollisionResult result)
		{
			result = default;
			if (!Enabled) return false;
			var oldPosition = Shape.Position;
			Shape.Position += motion;

			var didCollide = Shape.CollidesWithShape(collider.Shape, out result);
			if (didCollide)
				result.Collider = collider;

			Shape.Position = oldPosition;
			return didCollide;
		}


		/// <summary>
		/// checks to see if this Collider collides with any other Colliders in the Scene. The first Collider it intersects will have its collision
		/// data returned in the CollisionResult.
		/// </summary>
		/// <returns><c>true</c>, if with was collidesed, <c>false</c> otherwise.</returns>
		/// <param name="result">Result.</param>
		public bool CollidesWithAny(out CollisionResult result)
		{
			result = new CollisionResult();
			if (!Enabled) return false;

			// fetch anything that we might collide with at our new position
			var neighbors = Physics.BoxcastBroadphaseExcludingSelf(this, CollidesWithLayers);

			foreach (var neighbor in neighbors)
			{
				// skip triggers
				if (neighbor.IsTrigger)
					continue;

				if (CollidesWith(neighbor, out result))
					return true;
			}

			return false;
		}


		/// <summary>
		/// checks to see if this Collider with motion applied (delta movement vector) collides with any collider. If it does, true will be
		/// returned and result will be populated with collision data. Motion will be set to the maximum distance the Collider can travel
		/// before colliding.
		/// </summary>
		/// <returns><c>true</c>, if with was collidesed, <c>false</c> otherwise.</returns>
		/// <param name="motion">Motion.</param>
		/// <param name="result">Result.</param>
		public bool CollidesWithAny(ref Vector2 motion, out CollisionResult result)
		{
			result = new CollisionResult();
			if (!Enabled) return false;

			// fetch anything that we might collide with at our new position
			var colliderBounds = Bounds;
			colliderBounds.X += motion.X;
			colliderBounds.Y += motion.Y;
			var neighbors = Physics.BoxcastBroadphaseExcludingSelf(this, ref colliderBounds, CollidesWithLayers);

			var oldPosition = Shape.Position;
			Shape.Position += motion;

			var didCollide = false;
			foreach (var neighbor in neighbors)
			{
				// skip triggers
				if (neighbor.IsTrigger)
					continue;

				if (CollidesWith(neighbor, out CollisionResult neighborResult))
				{
					// hit. back off our motion and our Shape.position
					result = neighborResult;
					motion -= neighborResult.MinimumTranslationVector;
					Shape.Position -= neighborResult.MinimumTranslationVector;
					didCollide = true;
				}
			}

			Shape.Position = oldPosition;
			return didCollide;
		}

		#endregion

		public override Component Clone()
		{
			var collider = MemberwiseClone() as Collider;
			
			// Reset entity-specific state to ensure clean cloning
			collider.Entity = null;
			collider._isParentEntityAddedToScene = false;
			collider._isColliderRegistered = false;
			collider._isPositionDirty = true;
			collider._isRotationDirty = true;
			
			// Deep clone the shape if it exists
			if (Shape != null)
				collider.Shape = Shape.Clone();
			
			// Create a fresh component data instance to avoid shared references
			if (_data != null)
			{
				collider._data = new ColliderComponentData
				{
					IsTrigger = _data.IsTrigger,
					PhysicsLayer = _data.PhysicsLayer,
					CollidesWithLayers = _data.CollidesWithLayers,
					ShouldColliderScaleAndRotateWithTransform = _data.ShouldColliderScaleAndRotateWithTransform,
					Rectangle = _data.Rectangle,
					CircleRadius = _data.CircleRadius,
					CircleOffset = _data.CircleOffset,
					PolygonPoints = _data.PolygonPoints 
				};
			}
			
			return collider;
		}
	}
}
