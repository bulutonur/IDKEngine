#version 460 core

layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord;

struct Mesh
{
    int InstanceCount;
    int MatrixStart;
    int NodeStart;
    int BLASDepth;
    int MaterialIndex;
    float Emissive;
    float NormalMapStrength;
    float SpecularBias;
    float RoughnessBias;
    float RefractionChance;
};

layout(std430, binding = 2) restrict readonly buffer MeshSSBO
{
    Mesh Meshes[];
} meshSSBO;

layout(std430, binding = 4) restrict readonly buffer MatrixSSBO
{
    mat4 Models[];
} matrixSSBO;

layout(std140, binding = 0) uniform BasicDataUBO
{
    mat4 ProjView;
    mat4 View;
    mat4 InvView;
    vec3 ViewPos;
    int FreezeFramesCounter;
    mat4 Projection;
    mat4 InvProjection;
    mat4 InvProjView;
    mat4 PrevProjView;
    float NearPlane;
    float FarPlane;
    float DeltaUpdate;
} basicDataUBO;

layout(std140, binding = 5) uniform TaaDataUBO
{
    #define GLSL_MAX_TAA_UBO_VEC2_JITTER_COUNT 36 // used in shader and client code - keep in sync!
    vec4 Jitters[GLSL_MAX_TAA_UBO_VEC2_JITTER_COUNT / 2];
    int Samples;
    int Enabled;
    int Frame;
    float VelScale;
} taaDataUBO;

out InOutVars
{
    vec2 TexCoord;
    flat int MaterialIndex;
} outData;

void main()
{
    Mesh mesh = meshSSBO.Meshes[gl_DrawID];
    mat4 model = matrixSSBO.Models[mesh.MatrixStart + gl_InstanceID];

    outData.MaterialIndex = mesh.MaterialIndex;
    outData.TexCoord = TexCoord;

    vec3 fragPos = (model * vec4(Position, 1.0)).xyz;
    
    int rawIndex = taaDataUBO.Frame % taaDataUBO.Samples;
    vec2 offset = vec2(
        taaDataUBO.Jitters[rawIndex / 2][(rawIndex % 2) * 2 + 0],
        taaDataUBO.Jitters[rawIndex / 2][(rawIndex % 2) * 2 + 1]
    );

    vec4 jitteredClipPos = basicDataUBO.ProjView * vec4(fragPos, 1.0);
    jitteredClipPos.xy += offset * jitteredClipPos.w * taaDataUBO.Enabled;

    gl_Position = jitteredClipPos;
}