struct Batch { lo: vec3f, first: u32, hi: vec3f, count: u32 }
struct Params {
    origin: vec4f, right: vec4f, up: vec4f, forward: vec4f,
    sample: u32, face: u32, resolution: u32, spheres: u32,
    triangles: u32, rows: u32, reserved: vec2u,
}
@group(0) @binding(0) var<storage,read> original: array<u32>;
@group(0) @binding(1) var<storage,read> batches: array<Batch>;
@group(0) @binding(2) var<storage,read_write> offsets: array<vec2u>;
@group(0) @binding(3) var<storage,read_write> compacted: array<u32>;
@group(0) @binding(4) var<storage,read_write> draws: array<u32>;
@group(0) @binding(5) var<uniform> params: Params;
var<workgroup> totals: array<u32,128>;

fn outside(batch: Batch, normal: vec3f) -> bool {
    let p = select(batch.lo, batch.hi, normal >= vec3f(0)) - params.origin.xyz;
    return dot(p, normal) < -0.002;
}
@compute @workgroup_size(64) fn cullBatches(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= arrayLength(&batches)) { return; }
    let batch = batches[id.x];
    let near = max(max(batch.lo - params.origin.xyz, params.origin.xyz - batch.hi), vec3f(0));
    // Keep a conservative seam margin; never require a box corner inside the frustum.
    let forward = params.forward.xyz * (1.0 + 4.0 / f32(params.resolution));
    let culled = dot(near, near) > 1000.01 * 1000.01 || outside(batch, params.forward.xyz)
        || outside(batch, forward + params.right.xyz) || outside(batch, forward - params.right.xyz)
        || outside(batch, forward + params.up.xyz) || outside(batch, forward - params.up.xyz);
    offsets[id.x] = vec2u(select(batch.count, 0u, culled), 0);
}

@compute @workgroup_size(128) fn scanBatches(@builtin(local_invocation_index) lane: u32) {
    let count = arrayLength(&batches);
    let stride = (count + 127u) / 128u;
    let first = lane * stride;
    let last = min(first + stride, count);
    var sum = 0u;
    for (var i = first; i < last; i++) { sum += offsets[i].x; }
    totals[lane] = sum;
    workgroupBarrier();
    for (var step = 1u; step < 128u; step *= 2u) {
        var previous = 0u;
        if (lane >= step) { previous = totals[lane - step]; }
        workgroupBarrier();
        totals[lane] += previous;
        workgroupBarrier();
    }
    var prefix = totals[lane] - sum;
    for (var i = first; i < last; i++) {
        offsets[i].y = prefix;
        prefix += offsets[i].x;
    }
    if (lane == 127u) {
        draws[0] = 3;
        draws[1] = totals[127];
        draws[2] = 0;
        draws[3] = 0;
    }
}

@compute @workgroup_size(128) fn compactOrder(@builtin(workgroup_id) group: vec3u, @builtin(local_invocation_index) lane: u32) {
    let batch = batches[group.x];
    let span = offsets[group.x];
    if (lane >= span.x) { return; }
    compacted[span.y + lane] = original[batch.first + lane];
}
