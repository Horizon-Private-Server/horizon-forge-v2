import type { OcclusionGpuBufferFactory } from '../types/OcclusionWorker.js';
import { rasterBatches } from './RasterBounds.js';

export async function createSpatialCulling(
  device: GPUDevice, triangles: ArrayBuffer, originalOrder: Uint32Array<ArrayBuffer>,
  orderBuffer: GPUBuffer, params: GPUBuffer, make: OcclusionGpuBufferFactory,
  storage: (data: AllowSharedBufferSource) => GPUBuffer,
) {
  const bounds = rasterBatches(triangles, originalOrder), count = bounds.byteLength / 32;
  const batches = storage(bounds);
  const offsets = make(count * 8, GPUBufferUsage.STORAGE);
  const order = make(originalOrder.byteLength, GPUBufferUsage.STORAGE);
  const indirect = make(16, GPUBufferUsage.STORAGE | GPUBufferUsage.INDIRECT);
  const module = device.createShaderModule({ code: await (await fetch('./spatial.wgsl')).text() });
  const errors = (await module.getCompilationInfo()).messages.filter(message => message.type === 'error');
  if (errors.length) throw Error(errors.map(message => message.message).join('\n'));
  const groupLayout = device.createBindGroupLayout({ entries: [0, 1, 2, 3, 4, 5].map(binding => ({
    binding, visibility: GPUShaderStage.COMPUTE,
    buffer: binding === 5 ? { type: 'uniform', hasDynamicOffset: true, minBindingSize: 96 }
      : { type: binding < 2 ? 'read-only-storage' : 'storage' },
  })) });
  const layout = device.createPipelineLayout({ bindGroupLayouts: [groupLayout] });
  const [cull, scan, compact] = await Promise.all(['cullBatches', 'scanBatches', 'compactOrder']
    .map(entryPoint => device.createComputePipelineAsync({ layout, compute: { module, entryPoint } })));
  const group = device.createBindGroup({ layout: groupLayout,
    entries: [orderBuffer, batches, offsets, order, indirect, params].map((buffer, binding) => ({
      binding, resource: { buffer, ...(binding === 5 ? { size: 96 } : {}) },
    })) });

  return { order, indirect, encode(encoder: GPUCommandEncoder, offset: number, query: GPUQuerySet | null) {
    const pass = encoder.beginComputePass({
      timestampWrites: query ? { querySet: query, beginningOfPassWriteIndex: 0 } : undefined,
    });
    pass.setBindGroup(0, group, [offset]);
    pass.setPipeline(cull);
    pass.dispatchWorkgroups(Math.ceil(count / 64));
    pass.setPipeline(scan);
    pass.dispatchWorkgroups(1);
    pass.setPipeline(compact);
    pass.dispatchWorkgroups(count);
    pass.end();
  } };
}
