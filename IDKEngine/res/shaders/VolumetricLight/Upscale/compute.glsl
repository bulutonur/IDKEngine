#version 460 core
#extension GL_ARB_bindless_texture : require

AppInclude(include/Transformations.glsl)

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(binding = 0) restrict writeonly uniform image2D ImgResult;
layout(binding = 0) uniform sampler2D SamplerVolumetric;
layout(binding = 1) uniform sampler2D SamplerDepth;

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

layout(std140, binding = 6) uniform GBufferDataUBO
{
    sampler2D AlbedoAlpha;
    sampler2D NormalSpecular;
    sampler2D EmissiveRoughness;
    sampler2D Velocity;
    sampler2D Depth;
} gBufferDataUBO;

void main()
{
    ivec2 imgCoord = ivec2(gl_GlobalInvocationID.xy);
    vec2 uv = (imgCoord + 0.5) / imageSize(ImgResult);

    float highResDepth = LogarithmicDepthToLinearViewDepth(basicDataUBO.NearPlane, basicDataUBO.FarPlane, texture(gBufferDataUBO.Depth, uv).r) / basicDataUBO.FarPlane;

    vec3 color = vec3(0.0);

    // Source: https://www.alexandre-pestana.com/volumetric-lights/
    int xOffset = imgCoord.x % 2 == 0 ? -1 : 1;
    int yOffset = imgCoord.y % 2 == 0 ? -1 : 1;

    const ivec2 offsets[] = {
        ivec2(0, 0),
        ivec2(0, yOffset),
        ivec2(xOffset, 0),
        ivec2(xOffset, yOffset)
    };

    float totalWeight = 0.0;
    for (int i = 0; i < offsets.length(); i++)
    {
        ivec2 curPixel = imgCoord + offsets[i];
        vec2 sampleUv = (curPixel + 0.5) / imageSize(ImgResult);

        vec3 downscaledColor = texture(SamplerVolumetric, sampleUv).rgb;
        float lowResDepth = LogarithmicDepthToLinearViewDepth(basicDataUBO.NearPlane, basicDataUBO.FarPlane, texture(SamplerDepth, uv).r) / basicDataUBO.FarPlane;

        float currentWeight = max(0.0, 1.0 - 0.05 * abs(lowResDepth - highResDepth));

        color += downscaledColor * currentWeight;
        totalWeight += currentWeight;
    }

    color = color / (totalWeight + 0.0001);

    imageStore(ImgResult, imgCoord, vec4(color, 1.0));
}