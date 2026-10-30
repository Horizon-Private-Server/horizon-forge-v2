struct Triangle {
    a: vec3f, group: u32,
    e1: vec3f, flags: u32,
    e2: vec3f, textureIndex: u32,
    uvA: vec2f, uvB: vec2f, uvC: vec2f,
    wrapFlags: u32, rank: u32,
}
struct TextureInfo { offset: u32, width: u32, height: u32, padding: u32 }
struct Params {
    origin: vec4f, right: vec4f, up: vec4f, forward: vec4f,
    sample: u32, face: u32, resolution: u32, spheres: u32,
    triangles: u32, rows: u32, reserved: vec2u,
    barrierHeights: array<vec4f,4>, barrierCount: u32, pixelEdgeAllowance: u32,
}
struct Sphere { center: vec3f, group: u32, radius: f32, padding: array<u32,3> }
@group(0) @binding(0) var<storage,read> triangles: array<Triangle>;
@group(0) @binding(1) var<storage,read> order: array<u32>;
@group(0) @binding(2) var<storage,read> textures: array<TextureInfo>;
@group(0) @binding(3) var<storage,read> alphaWords: array<u32>;
@group(0) @binding(4) var<uniform> params: Params;
@group(1) @binding(0) var distanceImage: texture_2d<f32>;
@group(1) @binding(1) var ownerImage: texture_2d<u32>;
@group(1) @binding(2) var<storage,read_write> output: array<atomic<u32>>;
@group(1) @binding(3) var<storage,read> spheres: array<Sphere>;

struct Vertex {
    @builtin(position) position: vec4f,
    @location(0) relative: vec3f,
    @location(1) uv: vec2f,
    @location(2) @interpolate(flat) triangle: u32,
}
@vertex fn vertex(@builtin(vertex_index) corner: u32, @builtin(instance_index) instance: u32) -> Vertex {
    let index = order[instance];
    let t = triangles[index];
    let p = t.a + select(vec3f(0), select(t.e1, t.e2, corner == 2u), corner > 0u) - params.origin.xyz;
    let z = dot(p, params.forward.xyz);
    var out: Vertex;
    // Infinite projection; radial near/far limits are applied per fragment below.
    out.position = vec4f(dot(p, params.right.xyz), -dot(p, params.up.xyz), z - 0.000001, z);
    out.relative = p;
    out.uv = select(t.uvA, select(t.uvB, t.uvC, corner == 2u), corner > 0u);
    out.triangle = index;
    return out;
}

fn address(value: i32, size: u32, clampAxis: bool) -> u32 {
    if (clampAxis) { return u32(clamp(value, 0, i32(size) - 1)); }
    return u32(((value % i32(size)) + i32(size)) % i32(size));
}
fn texel(info: TextureInfo, x: i32, y: i32, flags: u32) -> f32 {
    let offset = info.offset + address(y, info.height, (flags & 2u) != 0u) * info.width
        + address(x, info.width, (flags & 1u) != 0u);
    return f32((alphaWords[offset / 4u] >> ((offset % 4u) * 8u)) & 255u);
}
fn opacity(t: Triangle, uvIn: vec2f) -> f32 {
    if (t.textureIndex == 0xffffffffu) { return 128; }
    let info = textures[t.textureIndex];
    var uv = uvIn;
    uv.x = select(fract(uv.x), clamp(uv.x, 0, 1), (t.wrapFlags & 1u) != 0u);
    uv.y = select(fract(uv.y), clamp(uv.y, 0, 1), (t.wrapFlags & 2u) != 0u);
    let p = uv * vec2f(f32(info.width), f32(info.height)) - vec2f(0.5);
    let at = vec2i(floor(p));
    let f = fract(p);
    let a = texel(info, at.x, at.y, t.wrapFlags);
    let b = texel(info, at.x, at.y + 1, t.wrapFlags);
    let top = a + (texel(info, at.x + 1, at.y, t.wrapFlags) - a) * f.x;
    let bottom = b + (texel(info, at.x + 1, at.y + 1, t.wrapFlags) - b) * f.x;
    return top + (bottom - top) * f.y;
}

struct Blocker { @location(0) distance: f32, @location(1) owner: u32, @builtin(frag_depth) depth: f32 }
@fragment fn blocker(in: Vertex) -> Blocker {
    let t = triangles[in.triangle];
    let d = length(in.relative);
    // Let a camera clipped into a surface see through its nearby back side.
    if (d <= 1.5 && dot(cross(t.e1, t.e2), in.relative) >= 0) { discard; }
    // Other opaque faces block from either side.
    if ((t.flags & 1u) == 0u || d < 0.01 || d > 1000 || opacity(t, in.uv) < 127) { discard; }
    // Depth ties retain the last primitive. Instances are submitted in reverse source order.
    return Blocker(d, t.rank, d / 1000.0);
}
fn mark(group: u32, moby: bool) {
    atomicOr(&output[params.sample * 64u + select(0u, 32u, moby) + group / 32u], 1u << (group % 32u));
}
fn barrierDistance(direction: vec3f) -> f32 {
    var limit = 1000.0;
    for (var i = 0u; i < params.barrierCount; i++) {
        let delta = params.barrierHeights[i / 4u][i % 4u] - params.origin.z;
        // A center exactly on the plane belongs to a cell spanning both sides.
        if (delta * direction.z > 0) { limit = min(limit, delta / direction.z); }
    }
    return limit;
}
fn visibilityDepth(pixel: vec2i, nearest: f32) -> f32 {
    if (params.reserved.x != 0u || params.pixelEdgeAllowance == 0u) { return nearest; }
    // Keep targets within two pixels of depth edges visible without raising resolution.
    var limit = nearest;
    for (var y = -2; y <= 2; y++) {
        for (var x = -2; x <= 2; x++) {
            let neighbor = clamp(pixel + vec2i(x, y), vec2i(0), vec2i(i32(params.resolution) - 1));
            limit = max(limit, textureLoad(distanceImage, neighbor, 0).x);
        }
    }
    return limit;
}

@fragment fn collect(in: Vertex) {
    let t = triangles[in.triangle];
    let d = length(in.relative);
    if (t.group >= 1024u || d < 0.01 || d > 1000) { discard; }
    // Barrier crossings are hard limits, independent of the pixel edge allowance.
    if (d > barrierDistance(in.relative / d) + 0.0001) { discard; }
    // One witness suffices for a baked group; calibration rays still need their exact hit.
    if (params.reserved.x == 0u && (atomicLoad(&output[params.sample * 64u + t.group / 32u])
        & (1u << (t.group % 32u))) != 0u) { discard; }
    let a = opacity(t, in.uv);
    if (a <= 0) { discard; }
    let pixel = vec2i(in.position.xy);
    let nearest = textureLoad(distanceImage, pixel, 0).x;
    let limit = visibilityDepth(pixel, nearest);
    if (d > limit + 0.01) { discard; }
    if (limit == nearest && d == nearest && a >= 127 && (t.flags & 1u) != 0u
        && t.rank != textureLoad(ownerImage, pixel, 0).x) { discard; }
    mark(t.group, false);
}

@compute @workgroup_size(8,8) fn mobys(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= params.resolution || id.y >= params.resolution) { return; }
    if (params.reserved.x != 0u && (id.x != params.resolution / 2u || id.y != params.resolution / 2u)) { return; }
    let uv = 2.0 * (vec2f(id.xy) + vec2f(0.5)) / f32(params.resolution) - vec2f(1);
    let direction = normalize(params.forward.xyz + uv.x * params.right.xyz + uv.y * params.up.xyz);
    let raw = textureLoad(distanceImage, vec2i(id.xy), 0).x;
    let nearest = visibilityDepth(vec2i(id.xy), raw);
    let barrier = barrierDistance(direction);
    for (var i = 0u; i < params.spheres; i++) {
        let s = spheres[i];
        let delta = params.origin.xyz - s.center;
        let b = dot(delta, direction);
        let c = dot(delta, delta) - s.radius * s.radius;
        let discriminant = b * b - c;
        if (discriminant < 0) { continue; }
        let root = sqrt(discriminant);
        let entry = max(0.01, -b - root);
        if (-b + root >= 0.01 && entry <= 1000) {
            if (entry > barrier + 0.0001) { continue; }
            if (entry <= nearest + 0.01) { mark(s.group, true); }
        }
    }
}
