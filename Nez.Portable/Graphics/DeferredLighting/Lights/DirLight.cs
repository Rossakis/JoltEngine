using Microsoft.VisualBasic.FileIO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Nez.Utils;
using static Nez.Sprites.SpriteRenderer.SpriteRendererComponentData;


namespace Nez.DeferredLighting
{
	/// <summary>
	/// directional light with a shortended name to avoid clashes with the XNA DirectionalLight. This light type has only a direction and is
	/// never culled. It is a global light and the only light that produces specular highlights.
	/// </summary>
	public class DirLight : DeferredLight
	{
		public class DirLightComponentData : ComponentData
		{
			public Vector3 Direction;
			public float SpecularIntensity;
			public float SpecularPower;
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

		private DirLightComponentData _data = new DirLightComponentData();

		public override ComponentData Data
		{
			get
			{
				_data.Enabled = Enabled;
				_data.Color = Color;
				_data.Direction = Direction;
				_data.SpecularIntensity = SpecularIntensity;
				_data.SpecularPower = SpecularPower;
				_data.DebugEnabled = DebugRenderEnabled;

				return _data;
			}
			set
			{
				if (value is DirLightComponentData d)
				{
					Enabled = d.Enabled;
					Color = d.Color;
					Direction = d.Direction;
					SpecularIntensity = d.SpecularIntensity;
					SpecularPower = d.SpecularPower;
					DebugRenderEnabled = d.DebugEnabled;

					_data = d;
				}
			}
		}


		// dir lights are infinite so bounds should be as well
		public override RectangleF Bounds => _bounds;

		/// <summary>
		/// direction of the light
		/// </summary>
		public Vector3 Direction = new Vector3(50, 20, 100);

		/// <summary>
		/// specular intensity. 0 - 1 range
		/// </summary>
		public float SpecularIntensity = 0.5f;

		/// <summary>
		/// specular power. this is the exponent passed to pow() of the projection from 0,0,-1 to the light-to-normal
		/// </summary>
		public float SpecularPower = 2;


		public DirLight()
		{
			_bounds = RectangleF.MaxRect;
		}


		public DirLight(Color color) : this()
		{
			Color = color;
		}


		public DirLight(Color color, Vector3 lightDirection) : this(color)
		{
			Direction = lightDirection;
		}

		/// <summary>
		/// we dont want to render our bounds so we just render a direction
		/// </summary>
		/// <param name="batcher">Batcher.</param>
		public override void DebugRender(Batcher batcher)
		{
			// figure out a starting corner for the line
			var root = Vector2.Zero;
			if (Direction.Y > 0)
				root.Y = 10f;
			else
				root.Y = Screen.Height - 10;

			if (Direction.X > 0)
				root.X = 10;
			else
				root.X = Screen.Width - 10;

			var angle = Mathf.Atan2(Direction.Y, Direction.X);
			batcher.DrawLineAngle(root, angle, 100, Color.Red, 3);
		}
	}
}