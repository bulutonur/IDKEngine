﻿using System;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using IDKEngine.Render.Objects;

namespace IDKEngine.Render
{
    class ModelSystem : IDisposable
    {
        public int TriangleCount => VertexIndices.Length / 3;


        public GpuDrawElementsCmd[] DrawCommands;
        private readonly BufferObject drawCommandBuffer;

        public GpuMesh[] Meshes;
        private readonly BufferObject meshBuffer;

        public GpuMeshInstance[] MeshInstances;
        private readonly BufferObject meshInstanceBuffer;

        public GpuMaterial[] Materials;
        private readonly BufferObject materialBuffer;

        public GpuVertex[] Vertices;
        private readonly BufferObject vertexBuffer;

        public Vector3[] VertexPositions;
        private readonly BufferObject vertexPositionBuffer;

        public uint[] VertexIndices;
        private readonly BufferObject vertexIndicesBuffer;

        public BVH BVH;

        private readonly VAO vao;
        private readonly ShaderProgram frustumCullingProgram;
        public unsafe ModelSystem()
        {
            DrawCommands = Array.Empty<GpuDrawElementsCmd>();
            drawCommandBuffer = new BufferObject();
            drawCommandBuffer.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0);

            Meshes = Array.Empty<GpuMesh>();
            meshBuffer = new BufferObject();
            meshBuffer.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1);

            MeshInstances = Array.Empty<GpuMeshInstance>();
            meshInstanceBuffer = new BufferObject();
            meshInstanceBuffer.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2);

            Materials = Array.Empty<GpuMaterial>();
            materialBuffer = new BufferObject();
            materialBuffer.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3);

            Vertices = Array.Empty<GpuVertex>();
            vertexBuffer = new BufferObject();
            vertexBuffer.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4);

            VertexPositions = Array.Empty<Vector3>();
            vertexPositionBuffer = new BufferObject();

            VertexIndices = Array.Empty<uint>();
            vertexIndicesBuffer = new BufferObject();
            vertexIndicesBuffer.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12);

            vao = new VAO();
            vao.SetElementBuffer(vertexIndicesBuffer);

            vao.AddSourceBuffer(vertexPositionBuffer, 0, sizeof(Vector3));
            vao.SetAttribFormat(0, 0, 3, VertexAttribType.Float, 0); // Position

            vao.AddSourceBuffer(vertexBuffer, 1, sizeof(GpuVertex));
            vao.SetAttribFormat(1, 1, 2, VertexAttribType.Float, 0); // TexCoord
            vao.SetAttribFormatI(1, 2, 1, VertexAttribType.UnsignedInt, sizeof(Vector2)); // Tangent
            vao.SetAttribFormatI(1, 3, 1, VertexAttribType.UnsignedInt, sizeof(Vector2) + sizeof(uint)); // Normal

            BVH = new BVH();

            frustumCullingProgram = new ShaderProgram(new Shader(ShaderType.ComputeShader, File.ReadAllText("res/shaders/Culling/SingleView/Frustum/compute.glsl")));
        }

        public unsafe void Add(params Model[] models)
        {
            if (models.Length == 0)
            {
                return;
            }

            for (int i = 0; i < models.Length; i++)
            {
                // Don't modify order
                LoadDrawCommands(models[i].DrawCommands);
                LoadVertices(models[i].Vertices);
                LoadVertexPositions(models[i].VertexPositions);

                // Don't modify order
                LoadMeshes(models[i].Meshes);
                LoadMaterials(models[i].Materials);

                LoadIndices(models[i].Indices);
                LoadModelMatrices(models[i].MeshInstances);
            }

            {
                int addedDrawCommands = models.Sum(model => model.DrawCommands.Length);
                int prevDrawCommandsLength = DrawCommands.Length - addedDrawCommands;
                ReadOnlyMemory<GpuDrawElementsCmd> newDrawCommands = new ReadOnlyMemory<GpuDrawElementsCmd>(DrawCommands, prevDrawCommandsLength, addedDrawCommands);
                BVH.AddMeshesAndBuild(newDrawCommands, DrawCommands, MeshInstances, VertexPositions, VertexIndices);

                // Caculate root node offset in blas buffer for each mesh
                uint bvhNodesExclusiveSum = 0;
                for (int i = 0; i < DrawCommands.Length; i++)
                {
                    DrawCommands[i].BlasRootNodeIndex = bvhNodesExclusiveSum;
                    bvhNodesExclusiveSum += (uint)BVH.Tlas.Blases[i].Nodes.Length;
                }
            }

            drawCommandBuffer.MutableAllocate(DrawCommands.Length * sizeof(GpuDrawElementsCmd), DrawCommands);
            meshBuffer.MutableAllocate(Meshes.Length * sizeof(GpuMesh), Meshes);
            meshInstanceBuffer.MutableAllocate(MeshInstances.Length * sizeof(GpuMeshInstance), MeshInstances);
            materialBuffer.MutableAllocate(Materials.Length * sizeof(GpuMaterial), Materials);
            vertexBuffer.MutableAllocate(Vertices.Length * sizeof(GpuVertex), Vertices);
            vertexPositionBuffer.MutableAllocate(VertexPositions.Length * sizeof(Vector3), VertexPositions);
            vertexIndicesBuffer.MutableAllocate(VertexIndices.Length * sizeof(uint), VertexIndices);
        }

        public unsafe void Draw()
        {
            if (Meshes.Length == 0)
            {
                return;
            }

            vao.Bind();
            drawCommandBuffer.Bind(BufferTarget.DrawIndirectBuffer);
            GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, 0, Meshes.Length, sizeof(GpuDrawElementsCmd));
        }

        public unsafe void FrustumCull(in Matrix4 projView)
        {
            if (Meshes.Length == 0)
            {
                return;
            }

            frustumCullingProgram.Use();
            frustumCullingProgram.Upload(0, projView);

            GL.DispatchCompute((Meshes.Length + 64 - 1) / 64, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit);
        }

        public unsafe void UpdateMeshBuffer(int start, int count)
        {
            if (count == 0) return;
            meshBuffer.SubData(start * sizeof(GpuMesh), count * sizeof(GpuMesh), Meshes[start]);
        }

        public unsafe void UpdateDrawCommandBuffer(int start, int count)
        {
            if (count == 0) return;
            drawCommandBuffer.SubData(start * sizeof(GpuDrawElementsCmd), count * sizeof(GpuDrawElementsCmd), DrawCommands[start]);
        }

        public unsafe void UpdateMeshInstanceBuffer(int start, int count)
        {
            if (count == 0) return;
            meshInstanceBuffer.SubData(start * sizeof(GpuMeshInstance), count * sizeof(GpuMeshInstance), MeshInstances[start]);
        }

        private void LoadDrawCommands(ReadOnlySpan<GpuDrawElementsCmd> drawCommands)
        {
            int prevCmdLength = DrawCommands.Length;
            int prevIndicesLength = DrawCommands.Length == 0 ? 0 : DrawCommands[prevCmdLength - 1].FirstIndex + DrawCommands[prevCmdLength - 1].Count;
            int prevBaseVertex = DrawCommands.Length == 0 ? 0 : DrawCommands[prevCmdLength - 1].BaseVertex + GetMeshVertexCount(prevCmdLength - 1);

            Array.Resize(ref DrawCommands, prevCmdLength + drawCommands.Length);
            drawCommands.CopyTo(new Span<GpuDrawElementsCmd>(DrawCommands, prevCmdLength, drawCommands.Length));

            for (int i = 0; i < drawCommands.Length; i++)
            {
                // TODO: Fix calculation of base instance to account for more than 1 instance per gltfModel
                DrawCommands[prevCmdLength + i].BaseInstance += prevCmdLength;
                DrawCommands[prevCmdLength + i].BaseVertex += prevBaseVertex;
                DrawCommands[prevCmdLength + i].FirstIndex += prevIndicesLength;
            }
        }
        private void LoadMeshes(ReadOnlySpan<GpuMesh> meshes)
        {
            int prevMeshesLength = Meshes.Length;
            int prevMaterialsLength = Materials.Length;
            Array.Resize(ref Meshes, prevMeshesLength + meshes.Length);
            meshes.CopyTo(new Span<GpuMesh>(Meshes, prevMeshesLength, meshes.Length));

            for (int i = 0; i < meshes.Length; i++)
            {
                Meshes[prevMeshesLength + i].MaterialIndex += prevMaterialsLength;
            }
        }
        private void LoadModelMatrices(ReadOnlySpan<GpuMeshInstance> meshInstances)
        {
            int prevMatricesLength = MeshInstances.Length;
            Array.Resize(ref MeshInstances, prevMatricesLength + meshInstances.Length);
            meshInstances.CopyTo(new Span<GpuMeshInstance>(MeshInstances, prevMatricesLength, meshInstances.Length));
        }
        private void LoadMaterials(ReadOnlySpan<GpuMaterial> materials)
        {
            int prevMaterialsLength = Materials.Length;
            Array.Resize(ref Materials, prevMaterialsLength + materials.Length);
            materials.CopyTo(new Span<GpuMaterial>(Materials, prevMaterialsLength, materials.Length));
        }
        private void LoadIndices(ReadOnlySpan<uint> indices)
        {
            int prevIndicesLength = VertexIndices.Length;
            Array.Resize(ref VertexIndices, prevIndicesLength + indices.Length);
            indices.CopyTo(new Span<uint>(VertexIndices, prevIndicesLength, indices.Length));
        }
        private void LoadVertices(ReadOnlySpan<GpuVertex> vertices)
        {
            int prevVerticesLength = Vertices.Length;
            Array.Resize(ref Vertices, prevVerticesLength + vertices.Length);
            vertices.CopyTo(new Span<GpuVertex>(Vertices, prevVerticesLength, vertices.Length));
        }

        private void LoadVertexPositions(ReadOnlySpan<Vector3> vertexPositions)
        {
            int prevVerticesLength = VertexPositions.Length;
            Array.Resize(ref VertexPositions, prevVerticesLength + vertexPositions.Length);
            vertexPositions.CopyTo(new Span<Vector3>(VertexPositions, prevVerticesLength, vertexPositions.Length));
        }

        public int GetMeshVertexCount(int meshIndex)
        {
            return ((meshIndex + 1 > DrawCommands.Length - 1) ? Vertices.Length : DrawCommands[meshIndex + 1].BaseVertex) - DrawCommands[meshIndex].BaseVertex;
        }

        public GpuTriangle GetTriangle(int indicesIndex, int baseVertex)
        {
            GpuTriangle triangle;
            triangle.Vertex0 = Vertices[VertexIndices[indicesIndex + 0] + baseVertex];
            triangle.Vertex1 = Vertices[VertexIndices[indicesIndex + 1] + baseVertex];
            triangle.Vertex2 = Vertices[VertexIndices[indicesIndex + 2] + baseVertex];
            return triangle;
        }

        public void Dispose()
        {
            drawCommandBuffer.Dispose();
            meshBuffer.Dispose();
            materialBuffer.Dispose();
            vertexBuffer.Dispose();
            vertexIndicesBuffer.Dispose();
            meshInstanceBuffer.Dispose();

            vao.Dispose();

            frustumCullingProgram.Dispose();
        }
    }
}
