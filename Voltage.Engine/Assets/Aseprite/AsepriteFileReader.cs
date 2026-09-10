using Microsoft.Xna.Framework.Content;

namespace Voltage.Aseprite
{
	/// <summary>Runtime side of the compiled Aseprite asset written by Voltage.Pipeline.</summary>
	public sealed class AsepriteFileReader : ContentTypeReader<AsepriteFile>
	{
		protected override AsepriteFile Read(ContentReader reader, AsepriteFile existingInstance) => AsepriteBinary.Read(reader);
	}
}
