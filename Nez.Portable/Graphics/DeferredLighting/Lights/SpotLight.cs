using System;
using Microsoft.Xna.Framework;


namespace Nez.DeferredLighting
{
	public class SpotLight : PointLight
	{
		public class SpotLightComponentData : ComponentData
		{
			public float Radius;
			public float Intensity;
			public float ConeAngle;

			public byte ColorR = 255;
			public byte ColorG = 255;
			public byte ColorB = 255;
			public byte ColorA = 255;
		}

		private SpotLightComponentData _data = new SpotLightComponentData();

		public override ComponentData Data
		{
			get
			{
				_data.Enabled = Enabled;
				_data.Radius = Radius;
				_data.Intensity = Intensity;
				_data.ConeAngle = ConeAngle;

				_data.ColorR = Color.R;
				_data.ColorG = Color.G;
				_data.ColorB = Color.B;
				_data.ColorA = Color.A;

				return _data;
			}
			set
			{
				if (value is SpotLightComponentData d)
				{
					Enabled = d.Enabled;
					SetRadius(d.Radius);          // ensure bounds update
					Intensity = d.Intensity;
					ConeAngle = d.ConeAngle;
					Color = new Color(d.ColorR, d.ColorG, d.ColorB, d.ColorA);
					_data = d;
				}
			}
		}

		/// <summary>
		/// wrapper for entity.transform.rotation to ease pointing at specific locations
		/// </summary>
		public Vector2 Direction => new Vector2(Mathf.Cos(Entity.Transform.Rotation), Mathf.Sin(Entity.Transform.Rotation));

		/// <summary>
		/// angle in degrees of the cone
		/// </summary>
		public float ConeAngle = 90f;


		public SpotLight() : base()
		{
		}

		public SpotLight(Color color)
		{
			Color = color;
		}


		#region Point light setters

		public new SpotLight SetZPosition(float z)
		{
			ZPosition = z;
			return this;
		}

		/// <summary>
		/// how far does this light reach
		/// </summary>
		/// <returns>The radius.</returns>
		/// <param name="radius">Radius.</param>
		public new SpotLight SetRadius(float radius)
		{
			base.SetRadius(radius);
			return this;
		}

		/// <summary>
		/// brightness of the light
		/// </summary>
		/// <returns>The intensity.</returns>
		/// <param name="intensity">Intensity.</param>
		public new SpotLight SetIntensity(float intensity)
		{
			Intensity = intensity;
			return this;
		}

		#endregion


		public SpotLight SetConeAngle(float coneAngle)
		{
			ConeAngle = coneAngle;
			return this;
		}

		/// <summary>
		/// wrapper for entity.transform.rotation to ease in setting up direction of spots to point at specific locations
		/// </summary>
		/// <returns>The direction.</returns>
		/// <param name="direction">Direction.</param>
		public SpotLight SetDirection(Vector2 direction)
		{
			Entity.Transform.Rotation = (float) Math.Atan2(direction.Y, direction.X);
			return this;
		}
	}
}