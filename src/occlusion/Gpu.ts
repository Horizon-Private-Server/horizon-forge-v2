import type { OcclusionCameraView, OcclusionVector, OcclusionWorkerApi, OcclusionWorkerScene } from '../types/OcclusionWorker.js';
import { hex } from './Scene.js';
import { cameraParameters, cameraParameterSize, cameraParameterStride, rasterResolution, sourceOrder } from './Cameras.js';
import { createSpatialCulling } from './SpatialGpu.js';

export const sampleBatchSize = 256;
const sampleMaskBytes = 256;
const timestampStride = 256;
const positionsPerSubmission = 8;

export async function createWorker(
  scene: { m: OcclusionWorkerScene; triangles: ArrayBuffer; textures: ArrayBuffer; alpha: ArrayBuffer },
  api: OcclusionWorkerApi,
) {
  if (!navigator.gpu)
    throw Error('WebGPU is unavailable. Enable hardware acceleration and update your graphics driver.');
  const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
  if (!adapter || ('isFallbackAdapter' in adapter && adapter.isFallbackAdapter === true) || /swiftshader|llvmpipe|lavapipe|software|warp/i.test(JSON.stringify(adapter.info)))
    throw Error('Occlusion baking requires a hardware GPU adapter.');
  const timestamps = adapter.features.has('timestamp-query');
  const device = await adapter.requestDevice({ requiredFeatures: timestamps ? ['timestamp-query'] : [] });
  const buffers: GPUBuffer[] = [], images: GPUTexture[] = [];
  let query: GPUQuerySet | null = null, failure: string | undefined;
  device.lost.then(result => { failure = result.message || 'GPU device lost'; });
  device.addEventListener('uncapturederror', event => { failure = event.error.message; });

  function healthy() {
    if (failure) throw Error(failure);
    if (api.cancelled()) throw Error('Occlusion bake cancelled.');
  }

  function make(size: number, usage: GPUBufferUsageFlags, data?: AllowSharedBufferSource) {
    const capacity = Math.max(4, size);
    if (capacity > device.limits.maxBufferSize
      || (usage & GPUBufferUsage.STORAGE) && capacity > device.limits.maxStorageBufferBindingSize)
      throw Error('Occlusion scene exceeds this GPU’s buffer limit.');
    const buffer = device.createBuffer({ size: capacity, usage });
    buffers.push(buffer);
    if (data?.byteLength) device.queue.writeBuffer(buffer, 0, data);
    return buffer;
  }
  const storage = (data: AllowSharedBufferSource) => make(data.byteLength, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST, data);

  function image(format: GPUTextureFormat) {
    const texture = device.createTexture({ size: [rasterResolution, rasterResolution], format,
      usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING });
    images.push(texture);
    return texture;
  }

  function destroy() {
    query?.destroy();
    for (const buffer of buffers) buffer.destroy();
    for (const texture of images) texture.destroy();
    device.destroy();
  }

  try {
    const manifest = scene.m;
    const triangles = storage(scene.triangles);
    const orderData = sourceOrder(scene.triangles), order = storage(orderData);
    const textures = make(Math.max(16, scene.textures.byteLength),
      GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST, scene.textures);
    const alpha = storage(scene.alpha);
    const maximum = cameraParameters(manifest, Array.from({ length: sampleBatchSize }, () => [0, 0, 0]));
    const params = make(maximum.bytes.byteLength, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
    const spatial = await createSpatialCulling(device, scene.triangles, orderData, order, params, make, storage);

    const packed = new ArrayBuffer(Math.max(32, manifest.spheres.length * 32)), sphereData = new DataView(packed);
    manifest.spheres.forEach((sphere, i) => {
      sphere.center.forEach((value, axis) => sphereData.setFloat32(i * 32 + axis * 4, value, true));
      sphereData.setUint32(i * 32 + 12, sphere.group, true);
      sphereData.setFloat32(i * 32 + 16, sphere.radius, true);
    });
    const spheres = storage(packed);
    const capacity = (sampleBatchSize + manifest.checks.length) * sampleMaskBytes;
    const output = make(capacity, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC | GPUBufferUsage.COPY_DST);
    const readback = make(capacity, GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST);
    const depthView = image('depth32float').createView();
    const distanceView = image('r32float').createView();
    const ownerView = image('r32uint').createView();

    const module = device.createShaderModule({ code: await (await fetch('./raster.wgsl')).text() });
    const errors = (await module.getCompilationInfo()).messages.filter(message => message.type === 'error');
    if (errors.length) throw Error(errors.map(message => message.message).join('\n'));
    const commonLayout = device.createBindGroupLayout({ entries: [
      { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE,
        buffer: { type: 'read-only-storage' } },
      { binding: 1, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
      ...[2, 3].map((binding): GPUBindGroupLayoutEntry => ({ binding, visibility: GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE,
        buffer: { type: 'read-only-storage' } })),
      { binding: 4, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE,
        buffer: { type: 'uniform', hasDynamicOffset: true, minBindingSize: cameraParameterSize } },
    ] });
    const targetLayout = device.createBindGroupLayout({ entries: [
      { binding: 0, visibility: GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE,
        texture: { sampleType: 'unfilterable-float' } },
      { binding: 1, visibility: GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE,
        texture: { sampleType: 'uint' } },
      { binding: 2, visibility: GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE, buffer: { type: 'storage' } },
      { binding: 3, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage' } },
    ] });
    const vertex = { module, entryPoint: 'vertex' };
    const primitive: GPUPrimitiveState = { topology: 'triangle-list', cullMode: 'none' };
    const layout = device.createPipelineLayout({ bindGroupLayouts: [commonLayout] });
    const targetPipelineLayout = device.createPipelineLayout({ bindGroupLayouts: [commonLayout, targetLayout] });
    const [blocker, collect, mobys] = await Promise.all([
      device.createRenderPipelineAsync({ layout, vertex, primitive,
        fragment: { module, entryPoint: 'blocker', targets: [{ format: 'r32float' }, { format: 'r32uint' }] },
        depthStencil: { format: 'depth32float', depthWriteEnabled: true, depthCompare: 'less-equal' } }),
      device.createRenderPipelineAsync({ layout: targetPipelineLayout, vertex, primitive,
        fragment: { module, entryPoint: 'collect', targets: [] },
        depthStencil: { format: 'depth32float', depthWriteEnabled: false, depthCompare: 'always' } }),
      device.createComputePipelineAsync({ layout: targetPipelineLayout, compute: { module, entryPoint: 'mobys' } }),
    ]);
    const common = device.createBindGroup({ layout: commonLayout,
      entries: [triangles, spatial.order, textures, alpha, params].map((buffer, binding) => ({ binding,
        resource: { buffer, ...(binding === 4 ? { size: cameraParameterSize } : {}) } })) });
    const targets = device.createBindGroup({ layout: targetLayout, entries: [
      { binding: 0, resource: distanceView },
      { binding: 1, resource: ownerView },
      { binding: 2, resource: { buffer: output } },
      { binding: 3, resource: { buffer: spheres } },
    ] });
    const passCount = manifest.spheres.length ? 3 : 2;
    query = timestamps ? device.createQuerySet({ type: 'timestamp', count: passCount * 2 }) : null;
    const times = query ? make(maximum.views.length * passCount * timestampStride,
      GPUBufferUsage.QUERY_RESOLVE | GPUBufferUsage.COPY_SRC) : null;
    const timesRead = times ? make(times.size, GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ) : null;
    const timestampWrites = (slot: number): GPUComputePassTimestampWrites | undefined => query ? {
      querySet: query, beginningOfPassWriteIndex: slot * 2, endOfPassWriteIndex: slot * 2 + 1,
    } : undefined;

    // These passes share attachments and preserve source ordering for equal-depth faces.
    function encodeView(encoder: GPUCommandEncoder, view: OcclusionCameraView, index: number) {
      const offset = index * cameraParameterStride;
      spatial.encode(encoder, offset, query);
      const block = encoder.beginRenderPass({
        colorAttachments: [
          { view: distanceView, clearValue: { r: 1000, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store' },
          { view: ownerView, clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store' },
        ],
        depthStencilAttachment: { view: depthView, depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'store' },
        timestampWrites: query ? { querySet: query, endOfPassWriteIndex: 1 } : undefined,
      });
      block.setPipeline(blocker);
      block.setBindGroup(0, common, [offset]);
      block.setViewport(0, 0, view.resolution, view.resolution, 0, 1);
      block.drawIndirect(spatial.indirect, 0);
      block.end();

      const visible = encoder.beginRenderPass({ colorAttachments: [],
        depthStencilAttachment: { view: depthView, depthReadOnly: true }, timestampWrites: timestampWrites(1) });
      visible.setPipeline(collect);
      visible.setBindGroup(0, common, [offset]);
      visible.setBindGroup(1, targets);
      visible.setViewport(0, 0, view.resolution, view.resolution, 0, 1);
      if (view.check) {
        const center = Math.floor(view.resolution / 2);
        visible.setScissorRect(center, center, 1, 1);
      }
      visible.drawIndirect(spatial.indirect, 0);
      visible.end();

      if (manifest.spheres.length) {
        const sphere = encoder.beginComputePass({ timestampWrites: timestampWrites(2) });
        sphere.setPipeline(mobys);
        sphere.setBindGroup(0, common, [offset]);
        sphere.setBindGroup(1, targets);
        sphere.dispatchWorkgroups(Math.ceil(view.resolution / 8), Math.ceil(view.resolution / 8));
        sphere.end();
      }
      if (query && times) for (let slot = 0; slot < passCount; slot++)
        encoder.resolveQuerySet(query, slot * 2, 2, times, (index * passCount + slot) * timestampStride);
    }

    async function run(samples: OcclusionVector[]) {
      const { views, bytes } = cameraParameters(manifest, samples);
      const size = (samples.length + manifest.checks.length) * sampleMaskBytes;
      healthy();
      device.queue.writeBuffer(params, 0, bytes);
      const clear = device.createCommandEncoder();
      clear.clearBuffer(output);
      device.queue.submit([clear.finish()]);
      for (let first = 0; first < views.length; first += positionsPerSubmission * 6) {
        healthy();
        // Bound queue waits so cancellation stays responsive without a wait per face.
        const end = Math.min(first + positionsPerSubmission * 6, views.length);
        const encoder = device.createCommandEncoder();
        for (let i = first; i < end; i++) encodeView(encoder, views[i], i);
        device.queue.submit([encoder.finish()]);
        await device.queue.onSubmittedWorkDone();
      }
      const encoder = device.createCommandEncoder();
      encoder.copyBufferToBuffer(output, 0, readback, 0, size);
      if (times && timesRead) encoder.copyBufferToBuffer(times, 0, timesRead, 0, views.length * passCount * timestampStride);
      device.queue.submit([encoder.finish()]);
      await Promise.all([readback.mapAsync(GPUMapMode.READ), ...(timesRead ? [timesRead.mapAsync(GPUMapMode.READ)] : [])]);
      try {
        healthy();
        const data = readback.getMappedRange();
        manifest.checks.forEach((check, i) => {
          for (const [offset, expected] of [[0, check.geometry], [128, check.mobys]] as const) {
            const start = (samples.length + i) * sampleMaskBytes + offset;
            if (hex(data.slice(start, start + 128)) !== expected)
              throw Error('GPU occlusion masks disagree with the CPU reference.');
          }
        });
        let gpuMs: number | null = null;
        if (timesRead) {
          gpuMs = 0;
          const timestamps = new DataView(timesRead.getMappedRange());
          for (let view = 0; view < samples.length * 6; view++) for (let slot = 0; slot < passCount; slot++) {
            const offset = (view * passCount + slot) * timestampStride;
            gpuMs += Number(timestamps.getBigUint64(offset + 8, true) - timestamps.getBigUint64(offset, true)) / 1e6;
          }
        }
        return { masks: new Uint8Array(data.slice(0, samples.length * sampleMaskBytes)), gpuMs };
      } finally {
        readback.unmap();
        timesRead?.unmap();
      }
    }

    await device.queue.onSubmittedWorkDone();
    healthy();
    return { destroy, run, adapter: { vendor: adapter.info.vendor, description: adapter.info.description } };
  } catch (error) {
    destroy();
    throw error;
  }
}
