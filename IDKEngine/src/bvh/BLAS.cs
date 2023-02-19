﻿#define USE_SAH
using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OpenTK.Mathematics;

namespace IDKEngine
{
    class BLAS
    {
        public const int MIN_TRIANGLES_PER_LEAF_COUNT = 3;
#if USE_SAH
        public const int SAH_SAMPLES = 8;
#endif

        public readonly GLSLBlasNode[] Nodes;

        private int nodesUsed;
        public unsafe BLAS(GLSLTriangle* triangles, int count, out int treeDepth)
        {
            treeDepth = (int)Math.Ceiling(MathF.Log2(count));
            
            Nodes = new GLSLBlasNode[2 * count];
            ref GLSLBlasNode root = ref Nodes[nodesUsed++];
            root.TriCount = (uint)count;

            nodesUsed++; // one empty node to align following childs in single 64 cache line (in theory)

            UpdateNodeBounds(ref root);
            Subdivide();

            void Subdivide(int nodeID = 0)
            {
                ref GLSLBlasNode parentNode = ref Nodes[nodeID];
                if (parentNode.TriCount <= MIN_TRIANGLES_PER_LEAF_COUNT)
                    return;
#if USE_SAH
                float splitSAH = FindBestSplitAxis(ref parentNode, out int splitAxis, out float splitPos);
                float parentSAH = CalculateSAH(MyMath.Area(parentNode.Max - parentNode.Min), parentNode.TriCount, 0, 0);
                if (splitSAH >= parentSAH)
                    return;
#else
                Vector3 extent = parentNode.Max - parentNode.Min;
                int splitAxis = 0;
                if (extent.Y > extent.X) splitAxis = 1;
                if (extent.Z > extent[splitAxis]) splitAxis = 2;

                float splitPos = parentNode.Min[splitAxis] + extent[splitAxis] * 0.5f;
#endif

                uint start = parentNode.TriStartOrLeftChild;
                uint end = start + parentNode.TriCount;

                uint mid = start;
                for (uint i = start; i < end; i++)
                {
                    ref GLSLTriangle tri = ref triangles[i];
                    if ((tri.Vertex0.Position[splitAxis] + tri.Vertex1.Position[splitAxis] + tri.Vertex2.Position[splitAxis]) * (1.0f / 3.0f) < splitPos)
                    {
                        Helper.Swap(ref tri, ref triangles[mid++]);
                    }
                }

                int leftChildID = nodesUsed++;
                int rightChildID = nodesUsed++;

                Nodes[leftChildID].TriStartOrLeftChild = start;
                Nodes[leftChildID].TriCount = mid - start;

                Nodes[rightChildID].TriStartOrLeftChild = mid;
                Nodes[rightChildID].TriCount = end - mid;

                parentNode.TriStartOrLeftChild = (uint)leftChildID;
                parentNode.TriCount = 0;

                UpdateNodeBounds(ref Nodes[leftChildID]);
                UpdateNodeBounds(ref Nodes[rightChildID]);

                Subdivide(leftChildID);
                Subdivide(rightChildID);
            }

#if USE_SAH
            float FindBestSplitAxis(ref GLSLBlasNode node, out int splitAxis, out float splitPos)
            {
                splitAxis = -1;
                splitPos = 0;

                AABB uniformDivideArea = new AABB(new Vector3(float.MaxValue), new Vector3(float.MinValue));
                for (int i = 0; i < node.TriCount; i++)
                {
                    ref readonly GLSLTriangle tri = ref triangles[(int)(node.TriStartOrLeftChild + i)];
                    Vector3 centroid = (tri.Vertex0.Position + tri.Vertex1.Position + tri.Vertex2.Position) * (1.0f / 3.0f);
                    uniformDivideArea.Shrink(centroid);
                }

                float bestSplitCost = float.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    if (uniformDivideArea.Min[i] == uniformDivideArea.Max[i])
                        continue;

                    float scale = (uniformDivideArea.Max[i] - uniformDivideArea.Min[i]) / SAH_SAMPLES;
                    for (int j = 1; j < SAH_SAMPLES; j++)
                    {
                        float currentSplitPos = uniformDivideArea.Min[i] + j * scale;
                        float currentSplitCost = GetSplitCost(node, i, currentSplitPos);
                        if (currentSplitCost < bestSplitCost)
                        {
                            splitPos = currentSplitPos;
                            splitAxis = i;
                            bestSplitCost = currentSplitCost;
                        }
                    }
                }

                return bestSplitCost;
            }

            float GetSplitCost(in GLSLBlasNode node, int splitAxis, float splitPos)
            {
                AABB leftBox = new AABB(new Vector3(float.MaxValue), new Vector3(float.MinValue));
                AABB rightBox = new AABB(new Vector3(float.MaxValue), new Vector3(float.MinValue));

                uint leftCount = 0;
                for (uint i = 0; i < node.TriCount; i++)
                {
                    ref readonly GLSLTriangle tri = ref triangles[(int)(node.TriStartOrLeftChild + i)];
                    float triSplitPos = (tri.Vertex0.Position[splitAxis] + tri.Vertex1.Position[splitAxis] + tri.Vertex2.Position[splitAxis]) * (1.0f / 3.0f);
                    if (triSplitPos < splitPos)
                    {
                        leftCount++;
                        leftBox.Shrink(tri);
                    }
                    else
                    {
                        rightBox.Shrink(tri);
                    }
                }
                uint rightCount = node.TriCount - leftCount;
                
                float sah = CalculateSAH(leftBox.Area(), leftCount, rightBox.Area(), rightCount);
                return sah;
            }
#endif

            void UpdateNodeBounds(ref GLSLBlasNode node)
            {
                Vector128<float> nodeMin = Vector128.Create(float.MaxValue);
                Vector128<float> nodeMax = Vector128.Create(float.MinValue);

                for (int i = 0; i < node.TriCount; i++)
                {
                    ref readonly GLSLTriangle tri = ref triangles[(int)(node.TriStartOrLeftChild + i)];

                    Vector128<float> v0 = Vector128.Create(tri.Vertex0.Position.X, tri.Vertex0.Position.Y, tri.Vertex0.Position.Z, 0.0f);
                    Vector128<float> v1 = Vector128.Create(tri.Vertex1.Position.X, tri.Vertex1.Position.Y, tri.Vertex1.Position.Z, 0.0f);
                    Vector128<float> v2 = Vector128.Create(tri.Vertex2.Position.X, tri.Vertex2.Position.Y, tri.Vertex2.Position.Z, 0.0f);

                    nodeMin = Sse.Min(nodeMin, v0);
                    nodeMin = Sse.Min(nodeMin, v1);
                    nodeMin = Sse.Min(nodeMin, v2);

                    nodeMax = Sse.Max(nodeMax, v0);
                    nodeMax = Sse.Max(nodeMax, v1);
                    nodeMax = Sse.Max(nodeMax, v2);
                }

                node.Min = nodeMin.AsVector3().ToOpenTK();
                node.Max = nodeMax.AsVector3().ToOpenTK();
            }
        }

        private static float CalculateSAH(float area0, uint triangles0, float area1, uint triangles1)
        {
            return area0 * triangles0 + area1 * triangles1;
        }
    }
}