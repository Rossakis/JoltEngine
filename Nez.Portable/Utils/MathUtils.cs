using System;
using Microsoft.Xna.Framework;

namespace Nez.Utils
{
    public class MathUtils
    {
        /// <summary>
        /// Returns true if either X or Y of the vector is NaN
        /// </summary>
        /// <param name="vector"></param>
        /// <returns></returns>
        public static bool IsVectorNaN(Vector2 vector)
        {
            return float.IsNaN(vector.X) || float.IsNaN(vector.Y);
        }

        public static bool IsVectorNaNOrInfinite(Vector2 vector)
        {
	        return float.IsNaN(vector.X) || float.IsNaN(vector.Y) || float.IsInfinity(vector.X) || float.IsInfinity(vector.Y);
        }

        public static bool IsVectorInfinite(Vector2 vector)
        {
	        return float.IsInfinity(vector.X) || float.IsInfinity(vector.Y);
		}

		public static bool IsNumBetween(float max, float min, float value)
        {
	        return value <= max && value >= min;
        }

		/// <summary>
		/// Returns true if the absolute value of 'value' is between the absolute values of 'min' and 'max'
		/// </summary>
		/// <param name="max"></param>
		/// <param name="min"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public static bool IsNumBetweenAbs(float max, float min, float value)
        {
	        return Math.Abs(value) <= Math.Abs(max) && Math.Abs(value) >= Math.Abs(min);
        }
	}
}
