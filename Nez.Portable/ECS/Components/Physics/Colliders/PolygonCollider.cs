using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Nez.PhysicsShapes;

namespace Nez
{
	/// <summary>
	/// Polygons should be defined in clockwise fashion.
	/// </summary>
	public class PolygonCollider : Collider
	{
		private List<Vector2> _points;

		/// <summary>
		/// Gets or sets the polygon points. When set, automatically updates the Shape.
		/// </summary>
		public List<Vector2> Points
		{
			get => _points;
			set
			{
				_points = value;
				UpdateShapeFromPoints();
			}
		}

		/// <summary>
		/// If the points are not centered they will be centered with the difference being applied to the localOffset.
		/// </summary>
		/// <param name="points">Points.</param>
		public PolygonCollider(Vector2[] points)
		{
			_points = new List<Vector2>();

			var isPolygonClosed = points[0] == points[points.Length - 1];
			if (isPolygonClosed)
				Array.Resize(ref points, points.Length - 1);

			var center = Polygon.FindPolygonCenter(points);
			SetLocalOffset(center);
			Polygon.RecenterPolygonVerts(points);
			Shape = new Polygon(points);
			
			_points.AddRange(points);
		}

		public PolygonCollider(int vertCount, float radius)
		{
			_points = new List<Vector2>();
			Shape = new Polygon(vertCount, radius);
			
			// Sync points from generated polygon
			if (Shape is Polygon poly)
				_points.AddRange(poly.Points);
		}

		public PolygonCollider() : this(6, 40)
		{
		}

		/// <summary>
		/// Updates the Shape from the current Points list.
		/// Call this after manually modifying Points.
		/// </summary>
		public void UpdateShapeFromPoints()
		{
			if (_points == null || _points.Count < 3)
			{
				Debug.Warn("PolygonCollider requires at least 3 points");
				return;
			}

			try
			{
				// Create shape directly from current points WITHOUT recentering
				var pointsArray = _points.ToArray();
				Shape = new Polygon(pointsArray);
				
				// Don't update _points or LocalOffset - keep them as-is
				// This preserves the barycentric center position
				
				if (Entity != null && Enabled)
				{
					_isPositionDirty = true;
					_isRotationDirty = true;
					Physics.UpdateCollider(this);
				}
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to update polygon shape: {ex.Message}");
			}
		}

		/// <summary>
		/// Updates shape WITH recentering - recalculates barycentric center and adjusts all points.
		/// Use this when you want to normalize the polygon or when loading from Tiled.
		/// </summary>
		public void UpdateShapeFromPointsWithRecentering()
		{
			if (_points == null || _points.Count < 3)
			{
				Debug.Warn("PolygonCollider requires at least 3 points");
				return;
			}

			try
			{
				var pointsArray = _points.ToArray();
				var center = Polygon.FindPolygonCenter(pointsArray);
				SetLocalOffset(center);
				Polygon.RecenterPolygonVerts(pointsArray);
				Shape = new Polygon(pointsArray);
				
				_points.Clear();
				_points.AddRange(pointsArray);
				
				if (Entity != null && Enabled)
				{
					_isPositionDirty = true;
					_isRotationDirty = true;
					Physics.UpdateCollider(this);
				}
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to update polygon shape: {ex.Message}");
			}
		}

		public override void DebugRender(Batcher batcher)
		{
			var poly = Shape as Polygon;
			if (poly == null)
				return;

			batcher.DrawHollowRect(Bounds, Debug.Colors.ColliderBounds, Debug.Size.LineSizeMultiplier);
			
			if(Enabled)
			{
				batcher.DrawPolygon(Shape.Position, poly.Points, Debug.Colors.ColliderEdge, true,
					Debug.Size.LineSizeMultiplier);
			}
			else if (!Enabled && IsVisibleEvenDisabled)
			{
				batcher.DrawPolygon(Shape.Position, poly.Points, Debug.Colors.ColliderDisabledModeEdge, true,
					Debug.Size.LineSizeMultiplier);
			}

			batcher.DrawPixel(Entity.Transform.Position, Debug.Colors.ColliderPosition,
				4 * Debug.Size.LineSizeMultiplier);
			batcher.DrawPixel(Shape.Position, Debug.Colors.ColliderCenter, 2 * Debug.Size.LineSizeMultiplier);
		}

		public override ComponentData Data
		{
			get
			{
				var polygonData = new ColliderComponentData
				{
					Enabled = Enabled,
					IsTrigger = IsTrigger,
					PhysicsLayer = PhysicsLayer,
					CollidesWithLayers = CollidesWithLayers,
					ShouldColliderScaleAndRotateWithTransform = ShouldColliderScaleAndRotateWithTransform,
					IsVisibleEvenDisabled = IsVisibleEvenDisabled,
					DebugEnabled = DebugRenderEnabled,
					LocalOffset = LocalOffset,
					PolygonPoints = _points != null ? new List<Vector2>(_points) : new List<Vector2>()
				};

				return polygonData;
			}
			set
			{
				if (value is ColliderComponentData colliderData)
				{
					// Unregister BEFORE modifying the shape
					if (_isColliderRegistered && Entity != null)
					{
						UnregisterColliderWithPhysicsSystem();
					}

					Enabled = colliderData.Enabled;
					IsTrigger = colliderData.IsTrigger;
					PhysicsLayer = colliderData.PhysicsLayer;
					CollidesWithLayers = colliderData.CollidesWithLayers;
					ShouldColliderScaleAndRotateWithTransform = colliderData.ShouldColliderScaleAndRotateWithTransform;
					IsVisibleEvenDisabled = colliderData.IsVisibleEvenDisabled;
					DebugRenderEnabled = colliderData.DebugEnabled;
					
					if (colliderData.PolygonPoints != null && colliderData.PolygonPoints.Count >= 3)
					{
						_points = new List<Vector2>(colliderData.PolygonPoints);
						
						// When loading, use recentering to normalize the polygon
						var pointsArray = _points.ToArray();
						var center = Polygon.FindPolygonCenter(pointsArray);
						SetLocalOffset(center);
						Polygon.RecenterPolygonVerts(pointsArray);
						Shape = new Polygon(pointsArray);
						
						_points.Clear();
						_points.AddRange(pointsArray);
					}
					else if (colliderData.LocalOffset != Vector2.Zero)
					{
						SetLocalOffset(colliderData.LocalOffset);
					}

					// IMPORTANT: Re-register AFTER modifying the shape
					if (Enabled && Entity != null)
					{
						RegisterColliderWithPhysicsSystem();
					}
				}
			}
		}
	}
}