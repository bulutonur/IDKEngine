AppInclude(include/Ray.glsl)
AppInclude(include/Box.glsl)
AppInclude(include/Frustum.glsl)

bool RayTriangleIntersect(Ray ray, vec3 v0, vec3 v1, vec3 v2, out vec3 bary, out float t)
{
    // Source: https://www.iquilezles.org/www/articles/intersectors/intersectors.htm

    vec3 v1v0 = v1 - v0;
    vec3 v2v0 = v2 - v0;
    vec3 rov0 = ray.Origin - v0;
    vec3 normal = cross(v1v0, v2v0);
    vec3 q = cross(rov0, ray.Direction);

    float x = dot(ray.Direction, normal);
    bary.yz = vec2(dot(-q, v2v0), dot(q, v1v0)) / x;
    bary.x = 1.0 - bary.y - bary.z;

    t = dot(-normal, rov0) / x;

    return all(greaterThanEqual(vec4(bary, t), vec4(0.0)));
}

bool RayBoxIntersect(Ray ray, Box box, out float t1, out float t2)
{
    // Source: https://medium.com/@bromanz/another-view-on-the-classic-ray-aabb-intersection-algorithm-for-bvh-traversal-41125138b525

    t1 = FLOAT_MIN;
    t2 = FLOAT_MAX;

    vec3 t0s = (box.Min - ray.Origin) / ray.Direction;
    vec3 t1s = (box.Max - ray.Origin) / ray.Direction;

    vec3 tsmaller = min(t0s, t1s);
    vec3 tbigger = max(t0s, t1s);

    t1 = max(t1, max(tsmaller.x, max(tsmaller.y, tsmaller.z)));
    t2 = min(t2, min(tbigger.x, min(tbigger.y, tbigger.z)));

    return t1 <= t2 && t2 > 0.0;
}

bool RaySphereIntersect(Ray ray, vec3 position, float radius, out float t1, out float t2)
{
    // Source: https://antongerdelan.net/opengl/raycasting.html
    
    t1 = FLOAT_MAX;
    t2 = FLOAT_MAX;

    vec3 sphereToRay = ray.Origin - position;
    float b = dot(ray.Direction, sphereToRay);
    float c = dot(sphereToRay, sphereToRay) - radius * radius;
    float discriminant = b * b - c;
    if (discriminant < 0.0)
    {
        return false;
    }

    float squareRoot = sqrt(discriminant);
    t1 = -b - squareRoot;
    t2 = -b + squareRoot;

    return t1 <= t2 && t2 > 0.0;
}

bool FrustumBoxIntersect(Frustum frustum, Box box)
{
    for (int i = 0; i < 6; i++)
    {
        vec3 negative = mix(box.Min, box.Max, greaterThan(frustum.Planes[i].xyz, vec3(0.0)));
        float a = dot(vec4(negative, 1.0), frustum.Planes[i]);

        if (a < 0.0)
        {
            return false;
        }
    }

    return true;
}

bool BoxBoxIntersect(Box a, Box b)
{
    return a.Min.x < b.Max.x &&
           a.Min.y < b.Max.y &&
           a.Min.z < b.Max.z &&

           a.Max.x > b.Min.x &&
           a.Max.y > b.Min.y &&
           a.Max.z > b.Min.z;
}

bool BoxDepthBufferIntersect(Box boxNdc, sampler2D samplerHiZ)
{
    vec2 boxUvMin = clamp(boxNdc.Min.xy * 0.5 + 0.5, vec2(0.0), vec2(1.0));
    vec2 boxUvMax = clamp(boxNdc.Max.xy * 0.5 + 0.5, vec2(0.0), vec2(1.0));

    ivec2 size = ivec2((boxUvMax - boxUvMin) * textureSize(samplerHiZ, 0));
    int level = int(ceil(log2(float(max(size.x, size.y)))));

    // Source: https://interplayoflight.wordpress.com/2017/11/15/experiments-in-gpu-based-occlusion-culling/
    // int lowerLevel = max(level - 1, 1);
    // float scale = exp2(-float(lowerLevel));
    // ivec2 a = ivec2(floor(boxUvMin * scale));
    // ivec2 b = ivec2(ceil(boxUvMax * scale));
    // ivec2 dims = b - a;
    // // Use the lower level if we only touch <= 2 texels in both dimensions
    // if (dims.x <= 2 && dims.y <= 2)
    // {
    //     level = lowerLevel;
    // }

    vec4 depths;
    depths.x = textureLod(samplerHiZ, boxUvMin, level).r;
    depths.y = textureLod(samplerHiZ, vec2(boxUvMax.x, boxUvMin.y), level).r;
    depths.w = textureLod(samplerHiZ, vec2(boxUvMin.x, boxUvMax.y), level).r;
    depths.z = textureLod(samplerHiZ, boxUvMax, level).r;

    float furthestDepth = max(max(depths.x, depths.y), max(depths.z, depths.w));
    float boxClosestDepth = clamp(boxNdc.Min.z, 0.0, 1.0);
    bool isVisible = boxClosestDepth <= furthestDepth;

    return isVisible;
}
