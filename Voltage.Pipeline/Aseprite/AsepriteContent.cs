using Voltage.Aseprite;

namespace Voltage.Pipeline.Aseprite;

/// <summary>A parsed Aseprite file on its way through the pipeline.</summary>
public sealed class AsepriteContent
{
	public AsepriteFile File { get; }

	public string SourcePath { get; }

	public AsepriteContent(AsepriteFile file, string sourcePath)
	{
		File = file;
		SourcePath = sourcePath;
	}
}
