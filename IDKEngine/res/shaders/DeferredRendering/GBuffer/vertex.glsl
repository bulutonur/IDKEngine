#version 460 core

layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 2) in uint Tangent;
layout(location = 3) in uint Normal;

AppInclude(shaders/include/Buffers.glsl)

out InOutVars
{
    vec2 TexCoord;
    vec4 ClipPos;
    vec4 PrevClipPos;
    vec3 Normal;
    mat3 TangentToWorld;
    uint MaterialIndex;
    float EmissiveBias;
    float NormalMapStrength;
    float SpecularBias;
    float RoughnessBias;
} outData;

vec3 DecompressSNorm32Fast(uint v);

void main()
{
    Mesh mesh = meshSSBO.Meshes[gl_DrawID];
    MeshInstance meshInstance = meshInstanceSSBO.MeshInstances[gl_InstanceID + gl_BaseInstance];
    
    vec3 normal = DecompressSNorm32Fast(Normal);
    vec3 tangent = DecompressSNorm32Fast(Tangent);
    
    vec3 T = normalize((meshInstance.ModelMatrix * vec4(tangent, 0.0)).xyz);
    vec3 N = normalize((meshInstance.ModelMatrix * vec4(normal, 0.0)).xyz);
    T = normalize(T - dot(T, N) * N);
    vec3 B = cross(N, T);

    outData.TangentToWorld = mat3(T, B, N);
    outData.TexCoord = TexCoord;
    vec3 worldPos = (meshInstance.ModelMatrix * vec4(Position, 1.0)).xyz;
    outData.ClipPos = basicDataUBO.ProjView * vec4(worldPos, 1.0);
    outData.PrevClipPos = basicDataUBO.PrevProjView * meshInstance.PrevModelMatrix * vec4(Position, 1.0);
    
    mat3 normalToWorld = mat3(transpose(meshInstance.InvModelMatrix));
    outData.Normal = normalize(normalToWorld * normal);
    outData.MaterialIndex = mesh.MaterialIndex;
    outData.EmissiveBias = mesh.EmissiveBias;
    outData.NormalMapStrength = mesh.NormalMapStrength;
    outData.SpecularBias = mesh.SpecularBias;
    outData.RoughnessBias = mesh.RoughnessBias;
    
    uint index = taaDataUBO.Frame % taaDataUBO.Samples;
    vec2 offset = vec2(
        taaDataUBO.Jitters[index / 2][(index % 2) * 2 + 0],
        taaDataUBO.Jitters[index / 2][(index % 2) * 2 + 1]
    );

    vec4 jitteredClipPos = outData.ClipPos;
    jitteredClipPos.xy += offset * outData.ClipPos.w * taaDataUBO.Enabled;
    
    gl_Position = jitteredClipPos;
}

vec3 DecompressSNorm32Fast(uint data)
{
    float r = (data >> 0) & ((1u << 11) - 1);
    float g = (data >> 11) & ((1u << 11) - 1);
    float b = (data >> 22) & ((1u << 10) - 1);

    r /= (1u << 11) - 1;
    g /= (1u << 11) - 1;
    b /= (1u << 10) - 1;

    return vec3(r, g, b) * 2.0 - 1.0;
}