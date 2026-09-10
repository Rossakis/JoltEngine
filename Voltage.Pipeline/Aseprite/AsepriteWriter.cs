using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Serialization.Compiler;
using Voltage.Aseprite;

namespace Voltage.Pipeline.Aseprite;

/// <summary>Writes the engine's compiled Aseprite payload; the runtime reader is <c>Voltage.Aseprite.AsepriteFileReader</c>.</summary>
[ContentTypeWriter]
public sealed class AsepriteWriter : ContentTypeWriter<AsepriteContent>
{
	protected override void Write(ContentWriter output, AsepriteContent value) => AsepriteBinary.Write(output, value.File);

	public override string GetRuntimeReader(TargetPlatform targetPlatform) => "Voltage.Aseprite.AsepriteFileReader, Voltage";

	public override string GetRuntimeType(TargetPlatform targetPlatform) => "Voltage.Aseprite.AsepriteFile, Voltage";
}
