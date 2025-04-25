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
    }
}
