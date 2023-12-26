#version 460 core
#extension GL_ARB_bindless_texture : require
#extension GL_AMD_gpu_shader_half_float : enable
#extension GL_AMD_gpu_shader_half_float_fetch : enable // requires GL_AMD_gpu_shader_half_float

#if GL_AMD_gpu_shader_half_float_fetch
#define HF_SAMPLER_2D f16sampler2D
#else
#define HF_SAMPLER_2D sampler2D
#endif

AppInclude(include/Constants.glsl)
AppInclude(include/Transformations.glsl)
AppInclude(include/Compression.glsl)
AppInclude(include/Random.glsl)
AppInclude(include/Ray.glsl)
AppInclude(PathTracing/include/Constants.glsl)

layout(local_size_x = N_HIT_PROGRAM_LOCAL_SIZE_X, local_size_y = 1, local_size_z = 1) in;

struct Material
{
    vec3 EmissiveFactor;
    uint BaseColorFactor;

    float _pad0;
    float AlphaCutoff;
    float RoughnessFactor;
    float MetallicFactor;

    HF_SAMPLER_2D BaseColor;
    HF_SAMPLER_2D MetallicRoughness;

    HF_SAMPLER_2D Normal;
    HF_SAMPLER_2D Emissive;
};

struct DrawElementsCmd
{
    uint IndexCount;
    uint InstanceCount;
    uint FirstIndex;
    uint BaseVertex;
    uint BaseInstance;

    uint BlasRootNodeIndex;
};

struct Mesh
{
    int MaterialIndex;
    float NormalMapStrength;
    float EmissiveBias;
    float SpecularBias;
    float RoughnessBias;
    float RefractionChance;
    float IOR;
    uint MeshletsStart;
    vec3 Absorbance;
    uint MeshletsCount;
};

struct MeshInstance
{
    mat4 ModelMatrix;
    mat4 InvModelMatrix;
    mat4 PrevModelMatrix;
};

struct Vertex
{
    vec2 TexCoord;
    uint Tangent;
    uint Normal;
};

struct BlasNode
{
    vec3 Min;
    uint TriStartOrLeftChild;
    vec3 Max;
    uint TriCount;
};

struct TlasNode
{
    vec3 Min;
    uint LeftChild;
    vec3 Max;
    uint BlasIndex;
};

struct WavefrontRay
{
    vec3 Origin;
    float PreviousIOROrDebugNodeCounter;

    vec3 Throughput;
    float CompressedDirectionX;

    vec3 Radiance;
    float CompressedDirectionY;
};

struct DispatchCommand
{
    uint NumGroupsX;
    uint NumGroupsY;
    uint NumGroupsZ;
};

struct Light
{
    vec3 Position;
    float Radius;
    vec3 Color;
    int PointShadowIndex;
};

layout(std430, binding = 0) restrict readonly buffer DrawElementsCmdSSBO
{
    DrawElementsCmd DrawCommands[];
} drawElementsCmdSSBO;

layout(std430, binding = 1) restrict readonly buffer MeshSSBO
{
    Mesh Meshes[];
} meshSSBO;

layout(std430, binding = 2) restrict readonly buffer MeshInstanceSSBO
{
    MeshInstance MeshInstances[];
} meshInstanceSSBO;

layout(std430, binding = 3) restrict readonly buffer MaterialSSBO
{
    Material Materials[];
} materialSSBO;

layout(std430, binding = 4) restrict readonly buffer VertexSSBO
{
    Vertex Vertices[];
} vertexSSBO;

layout(std430, binding = 5) restrict readonly buffer BlasSSBO
{
    BlasNode Nodes[];
} blasSSBO;

layout(std430, binding = 7) restrict readonly buffer TlasSSBO
{
    TlasNode Nodes[];
} tlasSSBO;

layout(std430, binding = 8) restrict buffer WavefrontRaySSBO
{
    WavefrontRay Rays[];
} wavefrontRaySSBO;

layout(std430, binding = 9) restrict buffer WavefrontPTSSBO
{
    DispatchCommand DispatchCommands[2];
    uint Counts[2];
    uint AccumulatedSamples;
    uint Indices[];
} wavefrontPTSSBO;

layout(std140, binding = 0) uniform BasicDataUBO
{
    mat4 ProjView;
    mat4 View;
    mat4 InvView;
    mat4 PrevView;
    vec3 ViewPos;
    uint Frame;
    mat4 Projection;
    mat4 InvProjection;
    mat4 InvProjView;
    mat4 PrevProjView;
    float NearPlane;
    float FarPlane;
    float DeltaRenderTime;
    float Time;
} basicDataUBO;

layout(std140, binding = 2) uniform LightsUBO
{
    Light Lights[GPU_MAX_UBO_LIGHT_COUNT];
    int Count;
} lightsUBO;

layout(std140, binding = 4) uniform SkyBoxUBO
{
    samplerCube Albedo;
} skyBoxUBO;

bool TraceRay(inout WavefrontRay wavefrontRay);
vec3 BounceOffMaterial(vec3 incomming, float specularChance, float roughness, float refractionChance, float ior, float prevIor, vec3 normal, bool fromInside, out float rayProbability, out float newIor, out bool isRefractive);
float FresnelSchlick(float cosTheta, float n1, float n2);

layout(location = 0) uniform int PingPongIndex;
uniform bool IsTraceLights;

AppInclude(PathTracing/include/BVHIntersect.glsl)
AppInclude(PathTracing/include/RussianRoulette.glsl)

void main()
{
    if (gl_GlobalInvocationID.x > wavefrontPTSSBO.Counts[1 - PingPongIndex])
    {
        return;
    }

    if (gl_GlobalInvocationID.x == 0)
    {
        wavefrontPTSSBO.DispatchCommands[1 - PingPongIndex].NumGroupsX = 0u;
    }

    InitializeRandomSeed(gl_GlobalInvocationID.x * 4096 + wavefrontPTSSBO.AccumulatedSamples);

    uint rayIndex = wavefrontPTSSBO.Indices[gl_GlobalInvocationID.x];
    WavefrontRay wavefrontRay = wavefrontRaySSBO.Rays[rayIndex];
    
    bool continueRay = TraceRay(wavefrontRay);
    wavefrontRaySSBO.Rays[rayIndex] = wavefrontRay;

    if (continueRay)
    {
        uint index = atomicAdd(wavefrontPTSSBO.Counts[PingPongIndex], 1u);
        wavefrontPTSSBO.Indices[index] = rayIndex;

        if (index % N_HIT_PROGRAM_LOCAL_SIZE_X == 0)
        {
            atomicAdd(wavefrontPTSSBO.DispatchCommands[PingPongIndex].NumGroupsX, 1u);
        }
    }
}

bool TraceRay(inout WavefrontRay wavefrontRay)
{
    vec3 uncompressedDir = DecompressOctahedron(vec2(wavefrontRay.CompressedDirectionX, wavefrontRay.CompressedDirectionY));

    HitInfo hitInfo;
    if (BVHRayTrace(Ray(wavefrontRay.Origin, uncompressedDir), hitInfo, IsTraceLights, FLOAT_MAX))
    {
        wavefrontRay.Origin += uncompressedDir * hitInfo.T;

        vec3 albedo;
        vec3 normal;
        vec3 emissive;
        float refractionChance;
        float specularChance;
        float roughness;
        float ior;
        vec3 absorbance;

        bool hitLight = hitInfo.VertexIndices == uvec3(0);
        if (!hitLight)
        {
            Vertex v0 = vertexSSBO.Vertices[hitInfo.VertexIndices.x];
            Vertex v1 = vertexSSBO.Vertices[hitInfo.VertexIndices.y];
            Vertex v2 = vertexSSBO.Vertices[hitInfo.VertexIndices.z];

            vec2 interpTexCoord = Interpolate(v0.TexCoord, v1.TexCoord, v2.TexCoord, hitInfo.Bary);
            vec3 interpNormal = normalize(Interpolate(DecompressSR11G11B10(v0.Normal), DecompressSR11G11B10(v1.Normal), DecompressSR11G11B10(v2.Normal), hitInfo.Bary));
            vec3 interpTangent = normalize(Interpolate(DecompressSR11G11B10(v0.Tangent), DecompressSR11G11B10(v1.Tangent), DecompressSR11G11B10(v2.Tangent), hitInfo.Bary));

            MeshInstance meshInstance = meshInstanceSSBO.MeshInstances[hitInfo.InstanceID];
            mat3 TBN = GetTBN(mat3(meshInstance.ModelMatrix), interpTangent, interpNormal);

            Mesh mesh = meshSSBO.Meshes[hitInfo.MeshID];
            Material material = materialSSBO.Materials[mesh.MaterialIndex];
            
            normal = texture(material.Normal, interpTexCoord).rgb;
            normal = TBN * normalize(normal * 2.0 - 1.0);
            mat3 normalToWorld = mat3(transpose(meshInstance.InvModelMatrix));
            normal = mix(normalize(normalToWorld * interpNormal), normal, mesh.NormalMapStrength);

            ior = mesh.IOR;
            absorbance = mesh.Absorbance;
            vec4 albedoAlpha = texture(material.BaseColor, interpTexCoord) * DecompressUR8G8B8A8(material.BaseColorFactor);
            albedo = albedoAlpha.rgb;
            emissive = MATERIAL_EMISSIVE_FACTOR * (texture(material.Emissive, interpTexCoord).rgb * material.EmissiveFactor) + mesh.EmissiveBias * albedo;
            
            refractionChance = clamp((1.0 - albedoAlpha.a) + mesh.RefractionChance, 0.0, 1.0);
            roughness = clamp(texture(material.MetallicRoughness, interpTexCoord).g * material.RoughnessFactor + mesh.RoughnessBias, 0.0, 1.0);
            if (albedoAlpha.a < material.AlphaCutoff)
            {
                refractionChance = 1.0;
                roughness = 0.0;
            }

            specularChance = clamp(texture(material.MetallicRoughness, interpTexCoord).r * material.MetallicFactor + mesh.SpecularBias, 0.0, 1.0 - refractionChance);
        }
        else
        {
            Light light = lightsUBO.Lights[hitInfo.MeshID];
            emissive = light.Color;
            albedo = light.Color;
            normal = (wavefrontRay.Origin - light.Position) / light.Radius;

            refractionChance = 0.0;
            specularChance = 1.0;
            roughness = 0.0;
            ior = 1.0;
            absorbance = vec3(0.0);
        }

        float cosTheta = dot(-uncompressedDir, normal);
        bool fromInside = cosTheta < 0.0;

        if (fromInside)
        {
            normal *= -1.0;
            cosTheta *= -1.0;

            wavefrontRay.Throughput *= exp(-absorbance * hitInfo.T);
        }

        if (specularChance > 0.0) // adjust specular chance based on view angle
        {
            float newSpecularChance = mix(specularChance, 1.0, FresnelSchlick(cosTheta, wavefrontRay.PreviousIOROrDebugNodeCounter, ior));
            float chanceMultiplier = (1.0 - newSpecularChance) / (1.0 - specularChance);
            refractionChance *= chanceMultiplier;
            specularChance = newSpecularChance;
        }
        
        wavefrontRay.Radiance += emissive * wavefrontRay.Throughput;

        float rayProbability, newIor;
        bool newRayRefractive;
        uncompressedDir = BounceOffMaterial(uncompressedDir, specularChance, roughness, refractionChance, ior, wavefrontRay.PreviousIOROrDebugNodeCounter, normal, fromInside, rayProbability, newIor, newRayRefractive);
        wavefrontRay.Origin += uncompressedDir * EPSILON;
        wavefrontRay.PreviousIOROrDebugNodeCounter = newIor;

        vec2 compressedDir = CompressOctahedron(uncompressedDir);
        wavefrontRay.CompressedDirectionX = compressedDir.x;
        wavefrontRay.CompressedDirectionY = compressedDir.y;

        if (!newRayRefractive)
        {
            wavefrontRay.Throughput *= albedo;
        }
        wavefrontRay.Throughput /= rayProbability;

        bool terminateRay = RussianRouletteTerminateRay(wavefrontRay.Throughput);
        return !terminateRay;
    }
    else
    {
        wavefrontRay.Radiance += texture(skyBoxUBO.Albedo, uncompressedDir).rgb * wavefrontRay.Throughput;
        return false;
    }
}

vec3 BounceOffMaterial(vec3 incomming, float specularChance, float roughness, float refractionChance, float ior, float prevIor, vec3 normal, bool fromInside, out float rayProbability, out float newIor, out bool isRefractive)
{
    isRefractive = false;
    roughness *= roughness;

    float rnd = GetRandomFloat01();
    vec3 diffuseRayDir = CosineSampleHemisphere(normal);
    vec3 outgoing;
    if (specularChance > rnd)
    {
        vec3 reflectionRayDir = reflect(incomming, normal);
        reflectionRayDir = normalize(mix(reflectionRayDir, diffuseRayDir, roughness));
        outgoing = reflectionRayDir;
        rayProbability = specularChance;
        newIor = prevIor;
    }
    else if (specularChance + refractionChance > rnd)
    {
        if (fromInside)
        {
            // we don't actually know wheter the next mesh we hit has ior 1.0
            newIor = 1.0;
        }
        else
        {
            newIor = ior;
        }
        vec3 refractionRayDir = refract(incomming, normal, prevIor / newIor);
        isRefractive = refractionRayDir != vec3(0.0);
        if (!isRefractive) // Total Internal Reflection
        {
            refractionRayDir = reflect(incomming, normal);
            newIor = prevIor;
        }
        refractionRayDir = normalize(mix(refractionRayDir, isRefractive ? -diffuseRayDir : diffuseRayDir, roughness));
        outgoing = refractionRayDir;
        rayProbability = refractionChance;
    }
    else
    {
        outgoing = diffuseRayDir;
        rayProbability = 1.0 - specularChance - refractionChance;
        newIor = prevIor;
    }
    rayProbability = max(rayProbability, EPSILON);

    return outgoing;
}

float FresnelSchlick(float cosTheta, float n1, float n2)
{
    float r0 = (n1 - n2) / (n1 + n2);
    r0 *= r0;

    return r0 + (1.0 - r0) * pow(1.0 - cosTheta, 5.0);
}

