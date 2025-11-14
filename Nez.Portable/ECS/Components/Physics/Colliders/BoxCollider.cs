using System.Linq;
using Microsoft.Xna.Framework;
using Nez.PhysicsShapes;


namespace Nez
{
	public class BoxCollider : Collider
	{
		[Inspectable]
		[Range(1, float.MaxValue, true)]
		public float Width
		{
			get
			{
				if (Shape is Box box)
					return box.Width;
				return 1f; // Default fallback
			}
			set => SetWidth(value);
		}

		[Inspectable]
		[Range(1, float.MaxValue, true)]
		public float Height
		{
			get
			{
				if (Shape is Box box)
					return box.Height;
				return 1f; // Default fallback
			}
			set => SetHeight(value);
		}


		/// <summary>
		/// zero param constructor requires that a RenderableComponent be on the entity so that the collider can size itself when the
		/// entity is added to the scene.
		/// </summary>
		public BoxCollider()
		{
			// we stick a 1x1 box in here as a placeholder until the next frame when the Collider is added to the Entity and can get more
			// accurate auto-sizing data
			Shape = new Box(1, 1);
			_colliderRequiresAutoSizing = true;
		}

		/// <summary>
		/// creates a BoxCollider and uses the x/y components as the localOffset
		/// </summary>
		/// <param name="x">The x coordinate.</param>
		/// <param name="y">The y coordinate.</param>
		/// <param name="width">Width.</param>
		/// <param name="height">Height.</param>
		public BoxCollider(float x, float y, float width, float height, string name = null)
		{
			Name = name ?? GetType().Name;
			_localOffset = new Vector2(x + width / 2, y + height / 2);
			Shape = new Box(width, height);
		}

		public BoxCollider(float width, float height, string name = null) : this(-width / 2, -height / 2, width, height,
			name)
		{
		}

		/// <summary>
		/// creates a BoxCollider and uses the x/y components of the Rect as the localOffset
		/// </summary>
		/// <param name="rect">Rect.</param>
		public BoxCollider(Rectangle rect, string name = null) : this(rect.X, rect.Y, rect.Width, rect.Height, name)
		{
		}


		#region Fluent setters

		/// <summary>
		/// sets the width of the BoxCollider
		/// </summary>
		/// <returns>The width.</returns>
		/// <param name="width">Width.</param>
		public BoxCollider SetWidth(float width)
		{
			_colliderRequiresAutoSizing = false;

			// Ensure we have a Box shape
			if (!(Shape is Box box))
			{
				Shape = new Box(width, Height);
				box = Shape as Box;
			}

			if (box != null && width != box.Width)
			{
				// update the box, dirty our bounds and if we need to update our bounds in the Physics system
				box.UpdateBox(width, box.Height);
				_isPositionDirty = true;
				if (Entity != null && _isParentEntityAddedToScene && Enabled)
					Physics.UpdateCollider(this);
			}

			return this;
		}

		/// <summary>
		/// sets the height of the BoxCollider
		/// </summary>
		/// <returns>The height.</returns>
		/// <param name="height">Height.</param>
		public BoxCollider SetHeight(float height)
		{
			_colliderRequiresAutoSizing = false;

			// Ensure we have a Box shape
			if (!(Shape is Box box))
			{
				Shape = new Box(Width, height);
				box = Shape as Box;
			}

			if (box != null && height != box.Height)
			{
				// update the box, dirty our bounds and if we need to update our bounds in the Physics system
				box.UpdateBox(box.Width, height);
				_isPositionDirty = true;
				if (Entity != null && _isParentEntityAddedToScene && Enabled)
					Physics.UpdateCollider(this);
			}

			return this;
		}

		/// <summary>
		/// sets the size of the BoxCollider
		/// </summary>
		/// <returns>The size.</returns>
		/// <param name="width">Width.</param>
		/// <param name="height">Height.</param>
		public BoxCollider SetSize(float width, float height)
		{
			_colliderRequiresAutoSizing = false;

			if (!(Shape is Box box))
			{
				Shape = new Box(width, height);
				box = Shape as Box;
			}

			if (box != null && (width != box.Width || height != box.Height))
			{
				// update the box, dirty our bounds and if we need to update our bounds in the Physics system
				box.UpdateBox(width, height);
				_isPositionDirty = true;
				if (Entity != null && _isParentEntityAddedToScene && Enabled)
					Physics.UpdateCollider(this);
			}

			return this;
		}

		#endregion


		public override void DebugRender(Batcher batcher)
		{
			if (!DebugRenderEnabled)
				return;

			var poly = Shape as Polygon;
			batcher.DrawHollowRect(Bounds, Debug.Colors.ColliderBounds, Debug.Size.LineSizeMultiplier);

			if (Enabled)
				batcher.DrawPolygon(Shape.Position, poly.Points, Debug.Colors.ColliderEdge, true,
					Debug.Size.LineSizeMultiplier);
			else if (!Enabled && IsVisibleEvenDisabled)
				batcher.DrawPolygon(Shape.Position, poly.Points, Debug.Colors.ColliderDisabledModeEdge, true,
					Debug.Size.LineSizeMultiplier);

			if (Entity == null)
				return;

			batcher.DrawPixel(Entity.Transform.Position, Debug.Colors.ColliderPosition,
				4 * Debug.Size.LineSizeMultiplier);
			batcher.DrawPixel(Entity.Transform.Position + Shape.Center, Debug.Colors.ColliderCenter,
				2 * Debug.Size.LineSizeMultiplier);
		}

		public string PrintBounds()
		{
			return string.Format("[BoxCollider: bounds: {0}", Bounds);
		}

		/// <summary>
		/// Creates a deep clone of this BoxCollider component.
		/// </summary>
		/// <returns>A new BoxCollider instance with all properties deep-copied</returns>
		public override Component Clone()
		{
			float currentWidth = Width;
			float currentHeight = Height;
			var clone = new BoxCollider(currentWidth, currentHeight, Name);

			// Copy all base Collider properties
			clone.LocalOffset = LocalOffset;
			clone.ShouldColliderScaleAndRotateWithTransform = ShouldColliderScaleAndRotateWithTransform;
			clone.IsTrigger = IsTrigger;
			clone.PhysicsLayer = PhysicsLayer;
			clone.CollidesWithLayers = CollidesWithLayers;
			clone.Enabled = Enabled;
			clone.IsVisibleEvenDisabled = IsVisibleEvenDisabled;
			clone.DebugRenderEnabled = DebugRenderEnabled;

			// Copy internal state flags
			clone._colliderRequiresAutoSizing = _colliderRequiresAutoSizing;
			clone._localOffsetLength = _localOffsetLength;

			// Reset entity-specific state (the clone isn't attached to any entity yet)
			clone.Entity = null;
			clone._isParentEntityAddedToScene = false;
			clone._isColliderRegistered = false;
			clone._isPositionDirty = true;
			clone._isRotationDirty = true;

			return clone;
		}
	}
}