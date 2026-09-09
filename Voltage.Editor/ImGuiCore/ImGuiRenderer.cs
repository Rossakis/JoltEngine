using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Voltage.Editor.ImGuiCore
{
	/// <summary>
	/// ImGui renderer for use with XNA-likes (FNA & MonoGame)
	/// </summary>
	public class ImGuiRenderer
	{
		public ImFontPtr DefaultFontPtr { get; private set; }

		// Graphics
		BasicEffect _effect;
		RasterizerState _rasterizerState;

		readonly VertexDeclaration _vertexDeclaration;
		readonly int _vertexDeclarationSize;

		byte[] _vertexData;
		VertexBuffer _vertexBuffer;
		int _vertexBufferSize;

		byte[] _indexData;
		IndexBuffer _indexBuffer;
		int _indexBufferSize;

		// Textures
		Dictionary<IntPtr, Texture2D> _loadedTextures = new Dictionary<IntPtr, Texture2D>();

		int _textureId;
		IntPtr? _fontTextureId;

		private ImGuiInput _input;

		// Experimental reinitialization support
		private bool _shouldReinitialize = false;

		public ImGuiRenderer(Game game)
		{
			_input = new ImGuiInput();

			unsafe
			{
				_vertexDeclarationSize = sizeof(ImDrawVert);
			}

			_vertexDeclaration = new VertexDeclaration(
				_vertexDeclarationSize,

				// Position
				new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),

				// UV
				new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),

				// Color
				new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0)
			);

			ImGui.SetCurrentContext(ImGui.CreateContext());

			_rasterizerState = new RasterizerState()
			{
				CullMode = CullMode.None,
				DepthBias = 0,
				FillMode = FillMode.Solid,
				MultiSampleAntiAlias = false,
				ScissorTestEnable = true,
				SlopeScaleDepthBias = 0
			};

			_input.SetupInput();
		}


		#region ImGuiRenderer

		/// <summary>
		/// Creates a texture and loads the font data from ImGui. Should be called when the <see cref="GraphicsDevice" /> is initialized but before any rendering is done
		/// </summary>
		public unsafe void RebuildFontAtlas(ImGuiOptions options)
		{
			// Get font texture from ImGui
			var io = ImGui.GetIO();

			// Get DPI scale from the primary monitor
			float dpiScale = GetPrimaryMonitorDpiScale();
			
			// Set proper font rendering settings BEFORE building atlas
			io.FontGlobalScale = 1.0f / dpiScale; // Adjust for DPI scaling

			if (options._includeDefaultFont)
			{
				unsafe
				{
					var config = ImGuiNative.ImFontConfig_ImFontConfig();
					var configPtr = new ImFontConfigPtr(config);
					configPtr.OversampleH = 3;
					configPtr.OversampleV = 1;
					configPtr.PixelSnapH = true;
					
					DefaultFontPtr = io.Fonts.AddFontDefault(config);
				}
			}

			foreach (var font in options._fonts)
			{
				unsafe
				{
					var config = ImGuiNative.ImFontConfig_ImFontConfig();
					var configPtr = new ImFontConfigPtr(config);
					configPtr.OversampleH = 3;
					configPtr.OversampleV = 1;
					configPtr.PixelSnapH = true;
					
					// Scale font size by DPI
					float scaledSize = font.Item2 * dpiScale;
					
					io.Fonts.AddFontFromFileTTF(font.Item1, scaledSize, config);
				}
			}

			// Build with proper flags for crisp rendering
			io.Fonts.Flags = ImFontAtlasFlags.NoPowerOfTwoHeight;
			io.Fonts.Build();

			io.Fonts.GetTexDataAsRGBA32(out byte* pixelData, out int width, out int height, out int bytesPerPixel);

			// Copy the data to a managed array
			var pixels = new byte[width * height * bytesPerPixel];
			Marshal.Copy(new IntPtr(pixelData), pixels, 0, pixels.Length);

			// Create texture with proper settings to avoid blur
			var tex2d = new Texture2D(Core.GraphicsDevice, width, height, false, SurfaceFormat.Color);
			tex2d.SetData(pixels);

			// Should a texture already have been built previously, unbind it first so it can be deallocated
			if (_fontTextureId.HasValue)
				UnbindTexture(_fontTextureId.Value);

			// Bind the new texture to an ImGui-friendly id
			_fontTextureId = BindTexture(tex2d);

			// Let ImGui know where to find the texture
			io.Fonts.SetTexID(_fontTextureId.Value);
			io.Fonts.ClearTexData(); // Clears CPU side texture data
		}

		/// <summary>
		/// Gets the DPI scale factor for the primary monitor
		/// </summary>
		private float GetPrimaryMonitorDpiScale()
		{
			try
			{
#if OS_WINDOWS
				// P/Invoke to get DPI
				IntPtr hdc = GetDC(IntPtr.Zero);
				int dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
				ReleaseDC(IntPtr.Zero, hdc);
				return dpiX / 96.0f; // 96 DPI is 100% scaling
#else
				return 1.0f; // Default for non-Windows platforms
#endif
			}
			catch
			{
				return 1.0f; // Fallback
			}
		}

#if OS_WINDOWS
		// Windows API constants and imports for DPI detection
		private const int LOGPIXELSX = 88;
		
		[DllImport("user32.dll")]
		private static extern IntPtr GetDC(IntPtr hWnd);
		
		[DllImport("user32.dll")]
		private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
		
		[DllImport("gdi32.dll")]
		private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
#endif

		/// <summary>
		/// Creates a pointer to a texture, which can be passed through ImGui calls such as <see cref="ImGui.Image" />. That pointer is then used by ImGui to let us know what texture to draw
		/// </summary>
		public IntPtr BindTexture(Texture2D texture)
		{
			var id = new IntPtr(_textureId++);
			_loadedTextures.Add(id, texture);
			return id;
		}


		/// <summary>
		/// Removes a previously created texture pointer, releasing its reference and allowing it to be deallocated
		/// </summary>
		public void UnbindTexture(IntPtr textureId)
		{
			_loadedTextures.Remove(textureId);
		}


		/// <summary>
		/// Sets up ImGui for a new frame, should be called at frame start
		/// </summary>
		public  void BeforeLayout(float deltaTime)
		{
			if (ImGui.GetCurrentContext() == IntPtr.Zero)
				return;

			// Ensure font atlas has been built
			if (!_fontTextureId.HasValue)
				return;

			var io = ImGui.GetIO();

			// Ensure display size is valid before starting a new frame
			if (io.DisplaySize.X <= 0 || io.DisplaySize.Y <= 0)
			{
				_input.UpdateInput();

				if (io.DisplaySize.X <= 0 || io.DisplaySize.Y <= 0)
					return;
			}

			io.DeltaTime = deltaTime;
			_input.UpdateInput();
			ImGui.NewFrame();
			FrameOpen = true;
		}

		/// <summary>True between NewFrame and Render.</summary>
		public bool FrameOpen { get; private set; }

		/// <summary>Ends a frame nothing will render, so a skipped Draw does not leave ImGui mid-frame.</summary>
		public void DiscardFrame()
		{
			if (!FrameOpen)
				return;
			ImGui.EndFrame();
			FrameOpen = false;
		}


		private void ReinitializeImGui()
		{
			// Recreate context
			ImGui.SetCurrentContext(ImGui.CreateContext());
			_input.SetupInput();

			// Rebuild font atlas
			var options = new ImGuiOptions();
			RebuildFontAtlas(options);
		}

		/// <summary>
		/// Asks ImGui for the generated geometry data and sends it to the graphics pipeline, should be called after the UI is drawn using ImGui.** calls
		/// </summary>
		public void AfterLayout()
		{
			ImGui.Render();
			FrameOpen = false;

			unsafe
			{
				// Reinitialize BEFORE getting draw data, not after
				if (_shouldReinitialize)
				{
					_shouldReinitialize = false;

					try
					{
						// Try to finish the current frame cleanly
						ImGui.EndFrame();
					}
					catch { }

					ReinitializeImGui();
					return;
				}
				RenderDrawData(ImGui.GetDrawData());
			}
		}

		#endregion


		#region Setup & Update

#if FNA
		delegate string GetClipboardTextDelegate();
		delegate void SetClipboardTextDelegate(IntPtr userData, string txt);

		static void SetClipboardText(IntPtr userData, string txt) => SDL2.SDL.SDL_SetClipboardText(txt);

		static string GetClipboardText() => SDL2.SDL.SDL_GetClipboardText();
#endif

		/// <summary>
		/// Updates the <see cref="Effect" /> to the current matrices and texture
		/// </summary>
		Effect UpdateEffect(Texture2D texture)
		{
			_effect = _effect ?? new BasicEffect(Core.GraphicsDevice);

			var io = ImGui.GetIO();

			_effect.World = Matrix.Identity;
			_effect.View = Matrix.Identity;
			_effect.Projection = Matrix.CreateOrthographicOffCenter(0, io.DisplaySize.X, io.DisplaySize.Y, 0, -1f, 1f);
			_effect.TextureEnabled = true;
			_effect.Texture = texture;
			_effect.VertexColorEnabled = true;

			return _effect;
		}
		#endregion


		#region Internals

		/// <summary>
		/// Gets the geometry as set up by ImGui and sends it to the graphics device
		/// </summary>
		void RenderDrawData(ImDrawDataPtr drawData)
		{
			// Setup render state: alpha-blending enabled, no face culling, no depth testing, scissor enabled, vertex/texcoord/color pointers
			var lastViewport = Core.GraphicsDevice.Viewport;
			var lastScissorBox = Core.GraphicsDevice.ScissorRectangle;
			var lastSamplerState = Core.GraphicsDevice.SamplerStates[0];

			Core.GraphicsDevice.BlendFactor = Color.White;
			Core.GraphicsDevice.BlendState = BlendState.NonPremultiplied;
			Core.GraphicsDevice.RasterizerState = _rasterizerState;
			Core.GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;

			// Handle cases of screen coordinates != from framebuffer coordinates (e.g. retina displays)
			drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

			// Setup projection
			Core.GraphicsDevice.Viewport = new Viewport(0, 0,
				Core.GraphicsDevice.PresentationParameters.BackBufferWidth,
				Core.GraphicsDevice.PresentationParameters.BackBufferHeight);

			UpdateBuffers(drawData);
			RenderCommandLists(drawData);

			// Restore modified state
			Core.GraphicsDevice.Viewport = lastViewport;
			Core.GraphicsDevice.ScissorRectangle = lastScissorBox;
			Core.GraphicsDevice.SamplerStates[0] = lastSamplerState;
		}

		unsafe void UpdateBuffers(ImDrawDataPtr drawData)
		{
			if (drawData.TotalVtxCount == 0)
			{
				return;
			}

			// Expand buffers if we need more room
			if (drawData.TotalVtxCount > _vertexBufferSize)
			{
				_vertexBuffer?.Dispose();

				_vertexBufferSize = (int)(drawData.TotalVtxCount * 1.5f);
				_vertexBuffer = new VertexBuffer(Core.GraphicsDevice, _vertexDeclaration, _vertexBufferSize,
					BufferUsage.None);
				_vertexData = new byte[_vertexBufferSize * _vertexDeclarationSize];
			}

			if (drawData.TotalIdxCount > _indexBufferSize)
			{
				_indexBuffer?.Dispose();

				_indexBufferSize = (int)(drawData.TotalIdxCount * 1.5f);
				_indexBuffer = new IndexBuffer(Core.GraphicsDevice, IndexElementSize.SixteenBits, _indexBufferSize,
					BufferUsage.None);
				_indexData = new byte[_indexBufferSize * sizeof(ushort)];
			}

			// Copy ImGui's vertices and indices to a set of managed byte arrays
			int vtxOffset = 0;
			int idxOffset = 0;

			for (var n = 0; n < drawData.CmdListsCount; n++)
			{
				var cmdList = drawData.CmdListsRange[n];

				fixed (void* vtxDstPtr = &_vertexData[vtxOffset * _vertexDeclarationSize])
				fixed (void* idxDstPtr = &_indexData[idxOffset * sizeof(ushort)])
				{
					Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, vtxDstPtr, _vertexData.Length,
						cmdList.VtxBuffer.Size * _vertexDeclarationSize);
					Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, idxDstPtr, _indexData.Length,
						cmdList.IdxBuffer.Size * sizeof(ushort));
				}

				vtxOffset += cmdList.VtxBuffer.Size;
				idxOffset += cmdList.IdxBuffer.Size;
			}

			// Copy the managed byte arrays to the gpu vertex- and index buffers
			_vertexBuffer.SetData(_vertexData, 0, drawData.TotalVtxCount * _vertexDeclarationSize);
			_indexBuffer.SetData(_indexData, 0, drawData.TotalIdxCount * sizeof(ushort));
		}

		unsafe void RenderCommandLists(ImDrawDataPtr drawData)
		{
			Core.GraphicsDevice.SetVertexBuffer(_vertexBuffer);
			Core.GraphicsDevice.Indices = _indexBuffer;

			// Base positions of the current cmd list's vertices/indices in the concatenated GPU buffers.
			int vtxBase = 0;
			int idxBase = 0;

			for (int n = 0; n < drawData.CmdListsCount; n++)
			{
				var cmdList = drawData.CmdListsRange[n];
				for (int cmdi = 0; cmdi < cmdList.CmdBuffer.Size; cmdi++)
				{
					var drawCmd = cmdList.CmdBuffer[cmdi];

					if (drawCmd.ElemCount == 0)
						continue;

					// Skip rendering if texture doesn't exist
					if (!_loadedTextures.ContainsKey(drawCmd.TextureId))
					{
						Debug.Warn($"Could not find a texture with id '{drawCmd.TextureId}'. Skipping draw call. This may happen after application restart.");
						continue;
					}

					Core.GraphicsDevice.ScissorRectangle = new Rectangle(
						(int)drawCmd.ClipRect.X,
						(int)drawCmd.ClipRect.Y,
						(int)(drawCmd.ClipRect.Z - drawCmd.ClipRect.X),
						(int)(drawCmd.ClipRect.W - drawCmd.ClipRect.Y)
					);

					var effect = UpdateEffect(_loadedTextures[drawCmd.TextureId]);
					foreach (var pass in effect.CurrentTechnique.Passes)
					{
						pass.Apply();

#pragma warning disable CS0618 // FNA does not expose an alternative method.
						// Use ImGui's per-command Idx/Vtx offsets — draw channels (used by modals, tables, columns)
						// make commands NON-contiguous, so accumulating ElemCount is wrong for those windows.
						Core.GraphicsDevice.DrawIndexedPrimitives(
							primitiveType: PrimitiveType.TriangleList,
							baseVertex: vtxBase + (int)drawCmd.VtxOffset,
							minVertexIndex: 0,
							numVertices: cmdList.VtxBuffer.Size,
							startIndex: idxBase + (int)drawCmd.IdxOffset,
							primitiveCount: (int)drawCmd.ElemCount / 3
						);
#pragma warning restore CS0618
					}
				}

				vtxBase += cmdList.VtxBuffer.Size;
				idxBase += cmdList.IdxBuffer.Size;
			}
		}

		#endregion
	}
}