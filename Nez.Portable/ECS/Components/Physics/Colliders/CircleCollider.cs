using System.Linq;
using Nez.PhysicsShapes;


namespace Nez
{
	public class CircleCollider : Collider
	{
		[Inspectable]
		public float Radius
		{
			get => ((Circle) Shape).Radius;
			set => SetRadius(value);
		}


		/// <summary>
		/// zero param constructor requires that a RenderableComponent be on the entity so that the collider can size itself when the
		/// entity is added to the scene.
		/// </summary>
		public CircleCollider()
		{
			// we stick a 1px circle in here as a placeholder until the next frame when the Collider is added to the Entity and can get more
			// accurate auto-sizing data
			Shape = new Circle(1);
			_colliderRequiresAutoSizing = true;
		}


		/// <summary>
		/// creates a CircleCollider with radius. Note that when specifying a radius if using a RenderableComponent on the Entity as well you
		/// will need to set the origin to align the CircleCollider. For example, if the RenderableComponent has a 0,0 origin and a CircleCollider
		/// with a radius of 1.5f * renderable.width is created you can offset the origin by just setting the originNormalied to the center
		/// divided by the scaled size:
		/// 
		/// 	entity.collider = new CircleCollider( moonTexture.Width * 1.5f );
		///     entity.collider.originNormalized = Vector2Extension.halfVector() / 1.5f;
		/// </summary>
		/// <param name="radius">Radius.</param>
		public CircleCollider(float radius)
		{
			Shape = new Circle(radius);
		}


		#region Fluent setters

		/// <summary>
		/// sets the radius for the CircleCollider
		/// </summary>
		/// <returns>The radius.</returns>
		/// <param name="radius">Radius.</param>
		public CircleCollider SetRadius(float radius)
		{
			_colliderRequiresAutoSizing = false;
			var circle = Shape as Circle;
			if (circle != null && radius != circle.Radius)
			{
				circle.Radius = radius;
				circle.OriginalRadius = radius;
				_isPositionDirty = true;

				if (Entity != null && _isParentEntityAddedToScene && Enabled)
					Physics.UpdateCollider(this);
			}

			return this;
		}

		#endregion


		public override void DebugRender(Batcher batcher)
		{
			batcher.DrawHollowRect(Bounds, Debug.Colors.ColliderBounds, Debug.Size.LineSizeMultiplier);

			if(Enabled)
				batcher.DrawCircle(Shape.Position, ((Circle) Shape).Radius, Debug.Colors.ColliderEdge,
				Debug.Size.LineSizeMultiplier);
			else if(!Enabled && IsVisibleEvenDisabled)
				batcher.DrawCircle(Shape.Position, ((Circle)Shape).Radius, Debug.Colors.ColliderDisabledModeEdge,
					Debug.Size.LineSizeMultiplier);

			batcher.DrawPixel(Entity.Transform.Position, Debug.Colors.ColliderPosition,
					4 * Debug.Size.LineSizeMultiplier);
			batcher.DrawPixel(Shape.Position, Debug.Colors.ColliderCenter, 2 * Debug.Size.LineSizeMultiplier);
		}

		public string PrintBounds()
		{
			return string.Format("[CircleCollider: bounds: {0}, radius: {1}", Bounds, ((Circle) Shape).Radius);
		}


		/// <summary>
		/// Creates a deep clone of this CircleCollider component.
		/// </summary>
		/// <returns>A new CircleCollider instance with all properties deep-copied</returns>
		public override Component Clone()
		{
			var clone = new CircleCollider();
			
			// Copy all base Collider properties
			clone.IsTrigger = IsTrigger;
			clone.PhysicsLayer = PhysicsLayer;
			clone.CollidesWithLayers = CollidesWithLayers;
			clone.ShouldColliderScaleAndRotateWithTransform = ShouldColliderScaleAndRotateWithTransform;
			clone.LocalOffset = LocalOffset;
			clone.Enabled = Enabled;
			clone.Name = Name;
			
			// Copy CircleCollider-specific properties
			clone.Radius = Radius;
			
			// Deep clone the Shape
			if (Shape != null)
			{
				clone.Shape = Shape.Clone();
			}
			
			// Copy internal state flags
			clone._colliderRequiresAutoSizing = _colliderRequiresAutoSizing;
			clone._localOffsetLength = _localOffsetLength;
			
			// Reset entity-specific state (the clone isn't attached to any entity yet)
			clone.Entity = null;
			clone._isParentEntityAddedToScene = false;
			clone._isColliderRegistered = false;
			clone._isPositionDirty = true;
			clone._isRotationDirty = true;
			
			// Copy the component data
			if (Data != null && Data is Collider.ColliderComponentData colliderData)
			{
				clone.Data = new Collider.ColliderComponentData
				{
					IsTrigger = colliderData.IsTrigger,
					PhysicsLayer = colliderData.PhysicsLayer,
					CollidesWithLayers = colliderData.CollidesWithLayers,
					ShouldColliderScaleAndRotateWithTransform = colliderData.ShouldColliderScaleAndRotateWithTransform,
					Rectangle = colliderData.Rectangle,
					CircleRadius = colliderData.CircleRadius, 
					CircleOffset = colliderData.CircleOffset,
					PolygonPoints = colliderData.PolygonPoints// Deep copy array if it exists
				};
			}
			
			return clone;
		}
	}
}