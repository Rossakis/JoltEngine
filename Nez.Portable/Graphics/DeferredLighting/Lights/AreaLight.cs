using Microsoft.Xna.Framework;
using Nez.Systems;


namespace Nez.DeferredLighting
{
	/// <summary>
	/// AreaLights work like DirLights except they only affect a specific area specified by the width/height. Note that Transform.scale
	/// will affect the size of an AreaLight.
	/// </summary>
	public class AreaLight : DeferredLight
	{
		public class AreaLightComponentData : ComponentData
		{
			public float Width;
			public float Height;
			public float Intensity;
			public Vector3 Direction;
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

		private AreaLightComponentData _data = new AreaLightComponentData();

		public override ComponentData Data
		{
			get
			{
				_data.Enabled = Enabled;
				_data.Color = Color;
				_data.Width = Width;
				_data.Height = Height;
				_data.Intensity = Intensity;
				_data.Direction = Direction;
				_data.DebugEnabled = DebugRenderEnabled;

				return _data;
			}
			set
			{
				if (value is AreaLightComponentData d)
				{
					Enabled = d.Enabled;
					Color = d.Color;
					_areaWidth = d.Width;
					_areaHeight = d.Height;
					Intensity = d.Intensity;
					Direction = d.Direction;
					_areBoundsDirty = true;
					DebugRenderEnabled = d.DebugEnabled;

					_data = d;
				}
			}
		}

		public override float Width => _areaWidth;
		public override float Height => _areaHeight;

		/// <summary>
		/// Override Bounds to properly calculate the world-space rectangle for this AreaLight
		/// </summary>
		public override RectangleF Bounds
		{
			get
			{
				if (_areBoundsDirty)
				{
					var scale = Entity.Transform.Scale;
					var width = _areaWidth * scale.X;
					var height = _areaHeight * scale.Y;
					
					_bounds.X = Entity.Transform.Position.X - width / 2f;
					_bounds.Y = Entity.Transform.Position.Y - height / 2f;
					_bounds.Width = width;
					_bounds.Height = height;
					
					_areBoundsDirty = false;
				}

				return _bounds;
			}
		}

		/// <summary>
		/// direction of the light
		/// </summary>
		public Vector3 Direction = new Vector3(500, 500, 50);

		/// <summary>
		/// brightness of the light
		/// </summary>
		public float Intensity = 12f;


		float _areaWidth, _areaHeight;

		public float RectangleWidth
		{
			get => _areaWidth;
			set => SetWidth(value);
		}
		
		public float RectangleHeight
		{
			get => _areaHeight;
			set => SetHeight(value);
		}

		public AreaLight() : this(200, 200)
		{
		}

		public AreaLight(float width, float height)
		{
			SetWidth(width).SetHeight(height);
		}

		public AreaLight SetWidth(float width)
		{
			_areaWidth = width;
			_areBoundsDirty = true;
			return this;
		}

		public AreaLight SetHeight(float height)
		{
			_areaHeight = height;
			_areBoundsDirty = true;
			return this;
		}
		
		/// <summary>
		/// Called when the entity's transform changes. Mark bounds as dirty.
		/// </summary>
		public override void OnEntityTransformChanged(Transform.Component comp)
		{
			_areBoundsDirty = true;
		}
	}
}