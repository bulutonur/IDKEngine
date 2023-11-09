﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using StbImageSharp;
using StbImageWriteSharp;
using IDKEngine.Render.Objects;

namespace IDKEngine
{
    static class Helper
    {
        public static readonly double APIVersion = Convert.ToDouble($"{GL.GetInteger(GetPName.MajorVersion)}{GL.GetInteger(GetPName.MinorVersion)}") / 10.0;
        public static readonly string API = GL.GetString(StringName.Version);
        public static readonly string GPU = GL.GetString(StringName.Renderer);

        public static int Sum<T>(this ReadOnlySpan<T> values, Func<T, int> func)
        {
            int sum = 0;
            for (int i = 0; i < values.Length; i++)
            {
                sum += func(values[i]);
            }
            return sum;
        }

        public static Vector4 ToOpenTK(this System.Numerics.Vector4 vector4)
        {
            return Unsafe.As<System.Numerics.Vector4, Vector4>(ref vector4);
        }

        public static System.Numerics.Vector3 ToNumerics(this Vector3 vector3)
        {
            return Unsafe.As<Vector3, System.Numerics.Vector3>(ref vector3);
        }
        public static Vector3 ToOpenTK(this System.Numerics.Vector3 vector3)
        {
            return Unsafe.As<System.Numerics.Vector3, Vector3>(ref vector3);
        }

        public static System.Numerics.Vector2 ToNumerics(this Vector2 vector2)
        {
            return Unsafe.As<Vector2, System.Numerics.Vector2>(ref vector2);
        }
        public static Vector2 ToOpenTK(this System.Numerics.Vector2 vector2)
        {
            return Unsafe.As<System.Numerics.Vector2, Vector2>(ref vector2);
        }

        public enum DepthConvention
        {
            ZeroToOne = ClipDepthMode.ZeroToOne,
            NegativeOneToOne = ClipDepthMode.NegativeOneToOne,
        }

        public static void SetDepthConvention(DepthConvention mode)
        {
            GL.ClipControl(ClipOrigin.LowerLeft, (ClipDepthMode)mode);
        }

        private static HashSet<string> GetExtensions()
        {
            HashSet<string> extensions = new HashSet<string>(GL.GetInteger(GetPName.NumExtensions));
            for (int i = 0; i < GL.GetInteger(GetPName.NumExtensions); i++)
            {
                extensions.Add(GL.GetString(StringNameIndexed.Extensions, i));
            }

            return extensions;
        }

        private static readonly HashSet<string> glExtensions = GetExtensions();
        public static bool IsExtensionsAvailable(string extension)
        {
            return glExtensions.Contains(extension);
        }
        public static bool IsCoreExtensionAvailable(string extension, double first)
        {
            return (APIVersion >= first) || IsExtensionsAvailable(extension);
        }
        public static unsafe void ParallelLoadCubemap(Texture texture, string[] paths, SizedInternalFormat sizedInternalFormat)
        {
            if (texture.Target != TextureTarget.TextureCubeMap)
            {
                Logger.Log(Logger.LogLevel.Error, $"Texture must be of type {TextureTarget.TextureCubeMap}");
                return;
            }
            if (paths.Length != 6)
            {
                Logger.Log(Logger.LogLevel.Error, "Number of cubemap images must be equal to six");
                return;
            }
            if (!paths.All(p => File.Exists(p)))
            {
                Logger.Log(Logger.LogLevel.Error, "At least one of the specified cubemap image paths is not found");
                return;
            }

            ImageResult[] images = new ImageResult[6];
            Parallel.For(0, images.Length, i =>
            {
                using FileStream stream = File.OpenRead(paths[i]);
                images[i] = ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlue);
            });
            
            if (!images.All(i => i.Width == i.Height && i.Width == images[0].Width))
            {
                Logger.Log(Logger.LogLevel.Error, "Cubemap images must be squares and each texture must be of the same size");
                return;
            }
            int size = images[0].Width;

            texture.ImmutableAllocate(size, size, 1, sizedInternalFormat);
            for (int i = 0; i < 6; i++)
            {
                texture.SubTexture3D(size, size, 1, PixelFormat.Rgb, PixelType.UnsignedByte, images[i].Data, 0, 0, 0, i);
            }
        }

        public static DebugProc GLDebugCallbackFuncPtr = GLDebugCallback;
        private static void GLDebugCallback(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userParam)
        {
            string text = Marshal.PtrToStringAnsi(message, length);
            switch (severity)
            {
                case DebugSeverity.DebugSeverityLow:
                    Logger.Log(Logger.LogLevel.Info, text);
                    break;

                case DebugSeverity.DebugSeverityMedium:
                    if (id == 0) return; // Shader compile warning, Intel
                    Logger.Log(Logger.LogLevel.Warn, text);
                    break;

                case DebugSeverity.DebugSeverityHigh:
                    if (id == 0) return; // Shader compile error, Intel
                    if (id == 2000) return; // Shader compile error, AMD
                    if (id == 2001) return; // Program link error, AMD

                    Logger.Log(Logger.LogLevel.Error, text);
                    break;

                case DebugSeverity.DebugSeverityNotification:
                    if (id == 131185) return; // Buffer detailed info, NVIDIA
                    Logger.Log(Logger.LogLevel.Info, text);
                    break;

                case DebugSeverity.DontCare:
                default:
                    break;
            }
        }


        public static unsafe T* Malloc<T>(int count = 1) where T : unmanaged
        {
            return (T*)Marshal.AllocHGlobal(sizeof(T) * count);
        }

        public static unsafe void Free(void* ptr)
        {
            Marshal.FreeHGlobal((IntPtr)ptr);
        }

        public static unsafe void MemSet(void* ptr, byte value, uint byteCount)
        {
            Unsafe.InitBlock(ptr, value, byteCount);
        }

        public static unsafe void MemCpy(void* dest, void* src, uint byteCount)
        {
            Unsafe.CopyBlock(dest, src, byteCount);
        }

        public static uint CompressSR11G11B10(in Vector3 data)
        {
            return CompressUR11G11B10(data * 0.5f + new Vector3(0.5f));
        }
        public static Vector3 DecompressSR11G11B10(uint data)
        {
            return DecompressUR11G11B10(data) * 2.0f - new Vector3(1.0f);
        }

        public static uint CompressUR11G11B10(in Vector3 data)
        {
            uint r = (uint)MathF.Round(data.X * ((1u << 11) - 1));
            uint g = (uint)MathF.Round(data.Y * ((1u << 11) - 1));
            uint b = (uint)MathF.Round(data.Z * ((1u << 10) - 1));

            uint compressed = (b << 22) | (g << 11) | (r << 0);

            return compressed;
        }
        public static Vector3 DecompressUR11G11B10(uint data)
        {
            float r = (data >> 0) & ((1u << 11) - 1);
            float g = (data >> 11) & ((1u << 11) - 1);
            float b = (data >> 22) & ((1u << 10) - 1);

            r *= (1.0f / ((1u << 11) - 1));
            g *= (1.0f / ((1u << 11) - 1));
            b *= (1.0f / ((1u << 10) - 1));

            return new Vector3(r, g, b);
        }

        public static uint CompressUR8G8B8A8(in Vector4 data)
        {
            uint r = (uint)MathF.Round(data.X * ((1u << 8) - 1));
            uint g = (uint)MathF.Round(data.Y * ((1u << 8) - 1));
            uint b = (uint)MathF.Round(data.Z * ((1u << 8) - 1));
            uint a = (uint)MathF.Round(data.W * ((1u << 8) - 1));

            uint compressed = (a << 24) | (b << 16) | (g << 8) | (r << 0);

            return compressed;
        }

        public delegate void FuncRunParallel(int i);
        public static Thread InParallel(int start, int endExclusive, FuncRunParallel func)
        {
            Thread thread = new Thread(() =>
            {
                Parallel.For(0, endExclusive, i =>
                {
                    func(start + i);
                });
            });
            thread.Start();
            return thread;
        }

        public static int InterlockedMax(ref int location1, int value)
        {
            int initialValue;
            int newValue;
            do
            {
                initialValue = location1;
                newValue = Math.Max(initialValue, value);
            }
            while (Interlocked.CompareExchange(ref location1, newValue, initialValue) != initialValue);
            
            return initialValue;
        }

        private static readonly Random rng = new Random();
        public static Vector3 RandomVec3(float min, float max)
        {
            return new Vector3(min) + new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * (max - min);
        }

        public static Vector3 RandomVec3(in Vector3 min, in Vector3 max)
        {
            return min + new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * (max - min);
        }

        public static float RandomFloat(float min, float max)
        {
            return min + rng.NextSingle() * (max - min);
        }

        public static unsafe void TextureToDisk(Texture texture, string path, int quality = 100, bool flipVertically = true)
        {
            StbImageWrite.stbi_flip_vertically_on_write(flipVertically ? 1 : 0);

            byte* pixels = Malloc<byte>(texture.Width * texture.Height * 3);
            texture.GetImageData(PixelFormat.Rgb, PixelType.UnsignedByte, (IntPtr)pixels, texture.Width * texture.Height * 3 * sizeof(byte));

            using FileStream fileStream = File.OpenWrite($"{path}.jpg");
            ImageWriter imageWriter = new ImageWriter();
            imageWriter.WriteJpg(pixels, texture.Width, texture.Height, StbImageWriteSharp.ColorComponents.RedGreenBlue, fileStream, quality);
            
            Free(pixels);
        }
    }
}