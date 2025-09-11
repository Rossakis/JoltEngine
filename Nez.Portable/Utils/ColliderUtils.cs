using Microsoft.Xna.Framework;
using Nez;
using Nez.PhysicsShapes;
using System;
using RectangleF = Nez.RectangleF;

public class ColliderUtils
{
	public static void SetColliderRectangle(Collider collider, RectangleF rectangle)
	{
		collider.LocalOffset = new Vector2(rectangle.X + rectangle.Width / 2f, rectangle.Y + rectangle.Height / 2f);
		collider.Shape = new Box(rectangle.Width, rectangle.Height);
	}

	/// <summary>
	/// Check if the RectangleF has different values than the Collider's RectF (not bounds necessarily)
	/// </summary>
	/// <param name="collider"></param>
	/// <param name="rectangle"></param>
	/// <returns></returns>
	public static bool HasColliderRectangleChanged(Collider collider, RectangleF rectangle)
	{
		if ((int)collider.Bounds.Width != (int)rectangle.Width || (int)collider.Bounds.Height != (int)rectangle.Height)
			return true;

		var rectOffsetX = (int)rectangle.X + (int)(rectangle.Width / 2f);
		var rectOffsetY = (int)rectangle.Y + (int)(rectangle.Height / 2f);

		if ((int)collider.LocalOffset.X != rectOffsetX ||
		    (int)collider.LocalOffset.Y != rectOffsetY)
			return true;

		return false;
	}

	/// <summary>
	/// Gets the local pos the Rectangle, depending on its origin point (e.g. Player.Position)
	/// </summary>
	public static RectangleF GetRectangleLocalPos(Vector2 originPoint, RectangleF rectangleF)
	{
		return new RectangleF(originPoint.X + rectangleF.X, originPoint.Y + rectangleF.Y, rectangleF.Width, rectangleF.Height);
	}

	public static Vector2 GetColliderPos(Collider collider)
	{
		return new Vector2(collider.AbsolutePosition.X + collider.Bounds.Width / 2f,
			collider.AbsolutePosition.Y + collider.Bounds.Height / 2f);
	}

	/// <summary>
	/// Returns either the top-right or top-left corner of the collider in world coordinates (assuming it's a BoxCollider).
	/// </summary>
	/// <param name="entity"></param>
	/// <param name="isRightCorner"></param>
	/// <returns></returns>
	/// <exception cref="ArgumentNullException"></exception>
	/// <exception cref="InvalidOperationException"></exception>
	public static Vector2 GetBoxColliderTopCornerPos(Entity entity, bool isRightCorner)
	{
		if (entity == null)
			throw new ArgumentNullException(nameof(entity));

		var collider = entity.GetComponent<Collider>();
		if (collider == null)
			throw new InvalidOperationException("Entity does not have a Collider component.");

		if(collider is not BoxCollider)
			throw new InvalidOperationException("Collider is not a BoxCollider!");

		var bounds = collider.Bounds;
		return isRightCorner
			? new Vector2(bounds.X + bounds.Width, bounds.Y)
			: new Vector2(bounds.X, bounds.Y);
	}

	public static Vector2 GetColliderCrossSectionPos(Collider a, Collider b)
	{
		if (a == null || b == null)
			throw new ArgumentNullException("Colliders cannot be null.");

		var boundsA = a.Bounds;
		var boundsB = b.Bounds;

		// Calculate intersection rectangle
		float left = Math.Max(boundsA.Left, boundsB.Left);
		float right = Math.Min(boundsA.Right, boundsB.Right);
		float top = Math.Max(boundsA.Top, boundsB.Top);
		float bottom = Math.Min(boundsA.Bottom, boundsB.Bottom);

		if (left < right && top < bottom)
		{
			// There is an intersection: return the center of the intersection rectangle
			float centerX = (left + right) / 2f;
			float centerY = (top + bottom) / 2f;
			return new Vector2(centerX, centerY);
		}
		else
		{
			// No intersection: return the closest points between the two bounds
			// Clamp the center of A to B's bounds
			var centerA = new Vector2(boundsA.Center.X, boundsA.Center.Y);
			float clampedX = Math.Clamp(centerA.X, boundsB.Left, boundsB.Right);
			float clampedY = Math.Clamp(centerA.Y, boundsB.Top, boundsB.Bottom);
			return new Vector2(clampedX, clampedY);
		}
	}
}
