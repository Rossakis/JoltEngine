using Microsoft.Xna.Framework;


namespace Nez.DeferredLighting
{
	/// <summary>
	/// PointLights radiate light in a circle. Note that PointLights are affected by Transform.scale. The Transform.scale.X value is multiplied
	/// by the lights radius when sent to the GPU. It is expected that scale will be linear.
	/// </summary>
	public class PointLight : DeferredLight
	{
		#region ComponentData
		public class PointLightComponentData : ComponentData
		{
			public float Radius;
			public float Intensity;
			public float ZPosition;
			public bool DebugEnabled; 

			public byte ColorR = 255;
			public byte ColorG = 255;
			public byte ColorB = 255;
			public byte ColorA = 255;
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
		}

		private PointLightComponentData _data = new PointLightComponentData();

		public override ComponentData Data
		{
			get
			{
				_data.Enabled = Enabled;
				_data.Radius = Radius;
				_data.Intensity = Intensity;
				_data.Color = Color;
				_data.ZPosition = ZPosition;
				_data.DebugEnabled = DebugRenderEnabled;

				return _data;
			}
			set
			{
				if (value is PointLightComponentData d)
				{
					Enabled = d.Enabled;
					SetRadius(d.Radius);       
					Intensity = d.Intensity;
					Color = d.Color;
					ZPosition = d.ZPosition;
					DebugRenderEnabled = d.DebugEnabled;

					_data = d;
				}
			}
		}
		#endregion

		public override RectangleF Bounds
		{
			get
			{
				if (_areBoundsDirty)
				{
					var size = Radius * Entity.Transform.Scale.X * 2;
					_bounds.CalculateBounds(Entity.Transform.Position, _localOffset, _radius * Entity.Transform.Scale,
						Vector2.One, 0, size, size);
					_areBoundsDirty = false;
				}

				return _bounds;
			}
		}

		/// <summary>
		/// "height" above the scene in the z direction
		/// </summary>
		public float ZPosition = 150f;

		/// <summary>
		/// how far does this light reach
		/// </summary>
		public float Radius => _radius;

		/// <summary>
		/// brightness of the light
		/// </summary>
		public float Intensity = 3f;

		protected float _radius;

		public PointLight()
		{
			SetRadius(400f);
		}

		public PointLight(Color color, float radius = 400) 
		{
			Color = color;
			SetRadius(radius);
		}

		public PointLight(float radius) 
		{
			SetRadius(radius);
		}

		/// <summary>
		/// how far does this light reach
		/// </summary>
		/// <returns>The radius.</returns>
		/// <param name="radius">Radius.</param>
		public PointLight SetRadius(float radius)
		{
			_radius = radius;
			_areBoundsDirty = true;

			return this;
		}

		/// <summary>
		/// renders the bounds only if there is no collider. Always renders a square on the origin.
		/// </summary>
		/// <param name="batcher">Batcher.</param>
		public override void DebugRender(Batcher batcher)
		{
			batcher.DrawCircle(Entity.Transform.Position + _localOffset, Radius * Entity.Transform.Scale.X, Color.DarkOrchid, 2);
		} 
	}
}