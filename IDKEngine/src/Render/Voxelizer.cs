﻿using System;
using System.IO;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using IDKEngine.Render.Objects;

namespace IDKEngine.Render
{
    class Voxelizer : IDisposable
    {
        public static readonly bool HAS_ATOMIC_FP16_VECTOR = Helper.IsExtensionsAvailable("GL_NV_shader_atomic_fp16_vector");
        public static readonly bool HAS_CONSERVATIVE_RASTER = Helper.IsExtensionsAvailable("GL_NV_conservative_raster");

        public unsafe Vector3 GridMin
        {
            get => glslVoxelizerData.GridMin;

            set
            {
                glslVoxelizerData.GridMin = value;
                glslVoxelizerData.OrthoProjection = Matrix4.CreateOrthographicOffCenter(glslVoxelizerData.GridMin.X, glslVoxelizerData.GridMax.X, glslVoxelizerData.GridMin.Y, glslVoxelizerData.GridMax.Y, glslVoxelizerData.GridMax.Z, glslVoxelizerData.GridMin.Z);
                voxelizerDataBuffer.SubData(0, sizeof(GLSLVoxelizerData), glslVoxelizerData);
            }
        }
        public unsafe Vector3 GridMax
        {
            get => glslVoxelizerData.GridMax;

            set
            {
                glslVoxelizerData.GridMax = value;
                glslVoxelizerData.OrthoProjection = Matrix4.CreateOrthographicOffCenter(glslVoxelizerData.GridMin.X, glslVoxelizerData.GridMax.X, glslVoxelizerData.GridMin.Y, glslVoxelizerData.GridMax.Y, glslVoxelizerData.GridMax.Z, glslVoxelizerData.GridMin.Z);
                voxelizerDataBuffer.SubData(0, sizeof(GLSLVoxelizerData), glslVoxelizerData);
            }
        }

        private float _debugStepMultiplier;
        public float DebugStepMultiplier
        {
            get => _debugStepMultiplier;

            set
            {
                _debugStepMultiplier = value;
                visualizeDebugProgram.Upload(0, _debugStepMultiplier);
            }
        }

        private float _debugConeAngle;
        public float DebugConeAngle
        {
            get => _debugConeAngle;

            set
            {
                _debugConeAngle = value;
                visualizeDebugProgram.Upload(1, _debugConeAngle);
            }
        }


        /// <summary>
        /// GL_NV_conservative_raster must be available for this to have an effect
        /// </summary>
        public bool IsConservativeRasterization;

        public Texture ResultVoxelsAlbedo;
        private readonly ShaderProgram resetTexturesProgram;
        private readonly ShaderProgram voxelizeProgram;
        private readonly ShaderProgram mipmapProgram;
        private readonly ShaderProgram visualizeDebugProgram;
        private readonly BufferObject voxelizerDataBuffer;
        private GLSLVoxelizerData glslVoxelizerData;

        private readonly Framebuffer fboNoAttachments;
        public unsafe Voxelizer(int width, int height, int depth, Vector3 gridMin, Vector3 gridMax, float debugConeAngle = 0.0f, float debugStepMultiplier = 0.2f)
        {
            resetTexturesProgram = new ShaderProgram(new Shader(ShaderType.ComputeShader, File.ReadAllText("res/shaders/Voxelize/Clear/compute.glsl")));

            voxelizeProgram = new ShaderProgram(
                new Shader(ShaderType.VertexShader, File.ReadAllText("res/shaders/Voxelize/vertex.glsl")),
                new Shader(ShaderType.GeometryShader, File.ReadAllText("res/shaders/Voxelize/geometry.glsl")),
                new Shader(ShaderType.FragmentShader, File.ReadAllText("res/shaders/Voxelize/fragment.glsl")));

            mipmapProgram = new ShaderProgram(new Shader(ShaderType.ComputeShader, File.ReadAllText("res/shaders/Voxelize/Mipmap/compute.glsl")));

            visualizeDebugProgram = new ShaderProgram(new Shader(ShaderType.ComputeShader, File.ReadAllText("res/shaders/Voxelize/Visualization/compute.glsl")));

            voxelizerDataBuffer = new BufferObject();
            voxelizerDataBuffer.ImmutableAllocate(sizeof(GLSLVoxelizerData), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            voxelizerDataBuffer.BindBufferBase(BufferRangeTarget.UniformBuffer, 5);

            fboNoAttachments = new Framebuffer();

            SetSize(width, height, depth);
            GridMin = gridMin;
            GridMax = gridMax;
            DebugConeAngle = debugConeAngle;
            DebugStepMultiplier = debugStepMultiplier;
        }

        public void Render(ModelSystem modelSystem)
        {
            ClearTextures();
            Voxelize(modelSystem);
            Mipmap();
        }

        private void ClearTextures()
        {
            ResultVoxelsAlbedo.BindToImageUnit(0, 0, true, 0, TextureAccess.WriteOnly, ResultVoxelsAlbedo.SizedInternalFormat);

            resetTexturesProgram.Use();
            GL.DispatchCompute((ResultVoxelsAlbedo.Width + 4 - 1) / 4, (ResultVoxelsAlbedo.Height + 4 - 1) / 4, (ResultVoxelsAlbedo.Depth + 4 - 1) / 4);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
        }

        private void Voxelize(ModelSystem modelSystem)
        {
            fboNoAttachments.Bind();

            if (HAS_CONSERVATIVE_RASTER && IsConservativeRasterization)
            {
                GL.Enable((EnableCap)All.ConservativeRasterizationNv);
            }

            GL.Viewport(0, 0, ResultVoxelsAlbedo.Width, ResultVoxelsAlbedo.Height);
            GL.ColorMask(false, false, false, false);
            GL.DepthMask(false);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            ResultVoxelsAlbedo.BindToImageUnit(0, 0, true, 0, TextureAccess.ReadWrite, ResultVoxelsAlbedo.SizedInternalFormat);

            //debugTimerQuery.Begin();

            voxelizeProgram.Use();
            modelSystem.Draw();
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.ColorMask(true, true, true, true);
            GL.DepthMask(true);

            if (HAS_CONSERVATIVE_RASTER && IsConservativeRasterization)
            {
                GL.Disable((EnableCap)All.ConservativeRasterizationNv);
            }

            //debugTimerQuery.End();
            //Console.WriteLine("Voxelized " + debugTimerQuery.MeasuredMilliseconds);
        }

        private void Mipmap()
        {
            ResultVoxelsAlbedo.BindToUnit(0);
            mipmapProgram.Use();

            int levels = Texture.GetMaxMipmapLevel(ResultVoxelsAlbedo.Width, ResultVoxelsAlbedo.Height, ResultVoxelsAlbedo.Depth);
            for (int i = 1; i < levels; i++)
            {
                ResultVoxelsAlbedo.BindToImageUnit(0, i, true, 0, TextureAccess.WriteOnly, ResultVoxelsAlbedo.SizedInternalFormat);

                Vector3i size = Texture.GetMipMapLevelSize(ResultVoxelsAlbedo.Width, ResultVoxelsAlbedo.Height, ResultVoxelsAlbedo.Depth, i);

                mipmapProgram.Upload(0, i - 1);
                GL.DispatchCompute((size.X + 4 - 1) / 4, (size.Y + 4 - 1) / 4, (size.Z + 4 - 1) / 4);
                GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
            }
        }

        public void DebugRender(Texture debugResult)
        {
            debugResult.BindToImageUnit(0, 0, false, 0, TextureAccess.WriteOnly, debugResult.SizedInternalFormat);
            ResultVoxelsAlbedo.BindToUnit(0);
            visualizeDebugProgram.Use();
            GL.DispatchCompute((debugResult.Width + 8 - 1) / 8, (debugResult.Height + 8 - 1) / 8, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
        }

        public void SetSize(int width, int height, int depth)
        {
            if (ResultVoxelsAlbedo != null) ResultVoxelsAlbedo.Dispose();
            ResultVoxelsAlbedo = new Texture(TextureTarget3d.Texture3D);
            ResultVoxelsAlbedo.SetFilter(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear);
            ResultVoxelsAlbedo.SetWrapMode(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
            ResultVoxelsAlbedo.SetAnisotropy(16.0f);
            ResultVoxelsAlbedo.ImmutableAllocate(width, height, depth, HAS_ATOMIC_FP16_VECTOR ? SizedInternalFormat.Rgba16f : SizedInternalFormat.Rgba8, Texture.GetMaxMipmapLevel(width, height, depth));

            fboNoAttachments.SetParamater(FramebufferDefaultParameter.FramebufferDefaultWidth, width);
            fboNoAttachments.SetParamater(FramebufferDefaultParameter.FramebufferDefaultHeight, height);
        }

        public void Dispose()
        {
            ResultVoxelsAlbedo.Dispose();

            resetTexturesProgram.Dispose();
            voxelizeProgram.Dispose();
            mipmapProgram.Dispose();
            visualizeDebugProgram.Dispose();

            voxelizerDataBuffer.Dispose();
        }
    }
}
