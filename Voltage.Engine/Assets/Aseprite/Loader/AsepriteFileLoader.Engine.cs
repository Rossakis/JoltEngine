using System;
using System.IO;

namespace Voltage.Aseprite
{
	public static partial class AsepriteFileLoader
	{
		/// <summary>
		/// Attempts to load an Aseprite (.ase/.aseprite) file and creates an instance with the contents of the file.
		/// </summary>
		/// <remarks>
		/// Errors occurred while attempting to load the Aseprite file will be logged in <see cref="Debug"/> output.
		/// </remarks>
		/// <param name="name">The name of the Aseprite (.ase/.aseprite) file to load</param>
		/// <param name="premultiplyAlpha">
		/// Indicates whether color data generated while reading the content of this Aseprite file should be
		/// premultipled.  Default is false.
		/// </param>
		/// <param name="file">
		/// When this method returns, if <see langword="true"/>, contains the contents of the Aseprite file that was
		/// loaded; otherwise, <see langword="null"/>.
		/// </param>
		/// <returns>
		/// <see langword="true"/> if the contents of the Aseprite file could be loaded without any issue; otherwise,
		/// <see langword="false"/>.
		/// </returns>
		public static bool TryLoad(string name, bool premultiplyAlpha, out AsepriteFile file)
		{
			file = null;

			try
			{
				using (var stream = File.OpenRead(name))
				using (var reader = new BinaryReader(stream))
					file = ReadAsepriteFile(reader, name, premultiplyAlpha);
			}
			catch (Exception ex)
			{
				Debug.Error(ex.Message);
			}

			return file != null;
		}
	}
}
