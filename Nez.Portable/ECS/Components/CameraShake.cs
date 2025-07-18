using System;
using Microsoft.Xna.Framework;
using Nez.Utils;

namespace Nez
{
	public class CameraShake : Component, IUpdatable
	{
		#region Component Data

		public class CameraShakeComponentData : ComponentData
		{
			public float TimePerFrame;
			public float FinishedThreshold;
			public bool Enabled;
			public float ShakeIntensity;
			public float ShakeDegradation;
		}

		private CameraShakeComponentData _data = new CameraShakeComponentData();

		public override ComponentData Data
		{
			get
			{
				_data.TimePerFrame = TimePerFrame;
				_data.FinishedThreshold = FinishedThreshold;
				_data.Enabled = Enabled;
				_data.ShakeIntensity = ShakeIntensity;
				_data.ShakeDegradation = ShakeDegradation;
				return _data;
			}
			set
			{
				if (value is CameraShakeComponentData shakeData)
				{
					TimePerFrame = shakeData.TimePerFrame;
					FinishedThreshold = shakeData.FinishedThreshold;
					Enabled = shakeData.Enabled;
					ShakeIntensity = shakeData.ShakeIntensity;
					ShakeDegradation = shakeData.ShakeDegradation;
					_data = shakeData;
				}
			}
		}

		#endregion

		// New properties for duration calculation
		public float TimePerFrame { get; set; } = 1f / 60f; // Default to 60 FPS
		public float TotalShakeDuration { get; private set; }
		public float FinishedThreshold { get; set; }

		public event Action ShakeFinished;

		Vector2 _shakeDirection;
		Vector2 _shakeOffset;

		private float _shakeIntensity = 0f;
		public float ShakeIntensity
		{
			get => _shakeIntensity;
			set => _shakeIntensity = value;
		}

		private float _shakeDegredation = 0.95f;
		public float ShakeDegradation
		{
			get => _shakeDegredation;
			set => _shakeDegredation = value;
		}

		public void OnShakeFinished()
		{
			ShakeFinished?.Invoke();
		}

		public CameraShake(float finishedThreshold = 0.01f, float nearlyFinishedThreshold = 0.1f)
		{
			FinishedThreshold = finishedThreshold;
		}

		public CameraShake() : base()
		{
		}

		public void Shake(
			float shakeIntensity = 15f,
			float shakeDegredation = 0.9f,
			Vector2 shakeDirection = default(Vector2)
		)
		{
			Enabled = true;
			if (_shakeIntensity < shakeIntensity)
			{
				_shakeDirection = shakeDirection;
				ShakeIntensity = shakeIntensity;

				// Validate degradation
				if (shakeDegredation < 0f || shakeDegredation >= 1f)
					shakeDegredation = 0.95f;

				ShakeDegradation = shakeDegredation;

				// Calculate total duration when new shake is applied
				CalculateTotalShakeDuration();
			}
		}

		public void Shake(Vector2 shakeDirection = default)
		{
			Enabled = true;
			// Use the current ShakeIntensity and ShakeDegradation properties
			if (_shakeIntensity < ShakeIntensity)
			{
				_shakeDirection = shakeDirection;

				// Validate degradation
				if (ShakeDegradation < 0f || ShakeDegradation >= 1f)
					ShakeDegradation = 0.95f;

				// Assign to backing fields to ensure consistency
				_shakeIntensity = ShakeIntensity;
				_shakeDegredation = ShakeDegradation;

				// Calculate total duration when new shake is applied
				CalculateTotalShakeDuration();
			}
		}

		private void CalculateTotalShakeDuration()
		{
			if (_shakeIntensity <= 0 || _shakeDegredation <= 0 || _shakeDegredation >= 1)
			{
				TotalShakeDuration = 0f;
				return;
			}

			// Formula: steps = ln(0.01 / intensity) / ln(degradation)
			double steps = Math.Log(0.01f / _shakeIntensity) / Math.Log(_shakeDegredation);
			steps = Math.Ceiling(steps);
			TotalShakeDuration = (float)steps * TimePerFrame;
		}

		public virtual void Update()
		{
			if (Math.Abs(_shakeIntensity) > 0f)
			{
				// Existing shake logic (unchanged)
				_shakeOffset = _shakeDirection;
				if (_shakeOffset.X != 0f || _shakeOffset.Y != 0f)
				{
					_shakeOffset.Normalize();
				}
				else
				{
					_shakeOffset.X = _shakeOffset.X + Random.NextFloat() - 0.5f;
					_shakeOffset.Y = _shakeOffset.Y + Random.NextFloat() - 0.5f;
				}

				_shakeOffset *= _shakeIntensity;
				_shakeIntensity *= -_shakeDegredation;

				//Finished
				if (Math.Abs(_shakeIntensity) <= FinishedThreshold)
				{
					_shakeIntensity = 0f;
					OnShakeFinished();
					Enabled = false;
				}
			}

			Entity.Scene.Camera.Position += _shakeOffset;
		}
	}
}