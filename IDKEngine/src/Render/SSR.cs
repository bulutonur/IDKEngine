﻿using OpenTK.Graphics.OpenGL4;
using IDKEngine.Render.Objects;

namespace IDKEngine.Render
{
    class SSR
    {
        private static ShaderProgram shaderProgram = new ShaderProgram(
            new Shader(ShaderType.ComputeShader, System.IO.File.ReadAllText("res/shaders/SSR/compute.glsl")));

        public readonly Texture Result;
        public SSR(int width, int height)
        {
            Result = new Texture(TextureTarget2d.Texture2D);
            Result.SetFilter(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            Result.MutableAllocate(width, height, 1, PixelInternalFormat.Rgba16f);
        }

        public unsafe void Compute(Texture samplerSrc, Texture normalTexture, Texture depthTexture, Texture cubemap)
        {
            Result.BindToImageUnit(0, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
            
            int* textures = stackalloc int[] { samplerSrc.ID, normalTexture.ID, depthTexture.ID, cubemap.ID };
            Texture.MultiBindToUnit(0, 4, textures);

            shaderProgram.Use();
            GL.DispatchCompute((Result.Width + 8 - 1) / 8, (Result.Height + 4 - 1) / 4, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
        }

        public void SetSize(int width, int height)
        {
            Result.MutableAllocate(width, height, 1, Result.PixelInternalFormat);
        }
    }
}