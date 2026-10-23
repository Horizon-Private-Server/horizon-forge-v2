import type {
  MobySimulation,
  MobySimulationDiagnostic,
  MobySimulationFactoryContext,
  MobySimulationInvalidation,
  MobySimulationProjectSnapshot,
  MobySimulationRendererServices,
  MobySimulationStats,
} from '../../types/MobySimulation.js';
import { MobySimulationRegistry } from './MobySimulationRegistry.ts';

interface ActiveSimulation {
  readonly classId: number;
  readonly simulation: MobySimulation;
  readonly unbindSelection: (() => void)[];
  readonly hiddenEntityIds: string[];
}

export interface MobySimulationControllerOptions {
  readonly fixedStepSeconds?: number;
  readonly maximumCatchUpSteps?: number;
}

export class MobySimulationController {
  private readonly registry: MobySimulationRegistry;
  private readonly services: MobySimulationRendererServices;
  private readonly active: ActiveSimulation[] = [];
  private readonly fixedStepSeconds: number;
  private readonly maximumCatchUpSteps: number;
  private snapshot?: MobySimulationProjectSnapshot;
  private enabled = false;
  private paused = false;
  private contextAvailable = true;
  private disposed = false;
  private generation = 0;
  private accumulator = 0;
  private timeSeconds = 0;
  private fixedTicks = 0;
  private droppedSeconds = 0;

  constructor(
    registry: MobySimulationRegistry,
    services: MobySimulationRendererServices,
    options: MobySimulationControllerOptions = {},
  ) {
    this.registry = registry;
    this.services = services;
    this.fixedStepSeconds = options.fixedStepSeconds ?? 1 / 60;
    this.maximumCatchUpSteps = options.maximumCatchUpSteps ?? 5;
    if (!Number.isFinite(this.fixedStepSeconds) || this.fixedStepSeconds <= 0)
      throw new Error('Moby simulation fixed step must be positive.');
    if (!Number.isSafeInteger(this.maximumCatchUpSteps) || this.maximumCatchUpSteps < 1)
      throw new Error('Moby simulation catch-up step limit must be a positive integer.');
  }

  supports(game: string, classId: number): boolean {
    this.ensureUsable();
    return this.registry.resolve(game, classId) !== undefined;
  }

  async setEnabled(enabled: boolean, snapshot?: MobySimulationProjectSnapshot): Promise<void> {
    this.ensureUsable();
    const previousSnapshot = this.snapshot;
    if (snapshot) this.snapshot = snapshot;
    if (this.enabled === enabled) {
      if (!enabled || !this.contextAvailable || !this.snapshot) return;
      if (snapshot && (previousSnapshot?.projectId !== snapshot.projectId
        || previousSnapshot.revision !== snapshot.revision)) {
        if (previousSnapshot?.projectId !== snapshot.projectId) this.resetClock();
        await this.recreateAll();
      } else if (this.active.length === 0) await this.createActive(this.snapshot);
      return;
    }
    this.enabled = enabled;
    if (!enabled) {
      this.generation += 1;
      this.clearActive();
      this.resetClock();
      return;
    }
    if (this.contextAvailable && this.snapshot) await this.createActive(this.snapshot);
  }

  async setSnapshot(snapshot: MobySimulationProjectSnapshot): Promise<void> {
    this.ensureUsable();
    const projectChanged = this.snapshot?.projectId !== snapshot.projectId;
    this.snapshot = snapshot;
    if (!this.enabled || !this.contextAvailable) return;
    if (projectChanged) this.resetClock();
    await this.recreateAll();
  }

  setPaused(paused: boolean): void {
    this.ensureUsable();
    this.paused = paused;
  }

  reset(): void {
    this.ensureUsable();
    this.resetClock();
    for (const entry of [...this.active])
      this.callSafely(entry, 'reset', () => entry.simulation.reset());
  }

  advance(deltaSeconds: number): void {
    this.ensureUsable();
    if (!this.enabled || this.paused || !this.contextAvailable || this.active.length === 0) return;
    const delta = Number.isFinite(deltaSeconds) ? Math.max(0, deltaSeconds) : 0;
    const maximumDelta = this.fixedStepSeconds * this.maximumCatchUpSteps;
    this.droppedSeconds += Math.max(0, delta - maximumDelta);
    this.accumulator += Math.min(delta, maximumDelta);
    let steps = 0;
    while (this.accumulator >= this.fixedStepSeconds && steps < this.maximumCatchUpSteps) {
      this.accumulator -= this.fixedStepSeconds;
      this.timeSeconds += this.fixedStepSeconds;
      this.fixedTicks += 1;
      steps += 1;
      const update = {
        stepSeconds: this.fixedStepSeconds,
        tick: this.fixedTicks,
        timeSeconds: this.timeSeconds,
      };
      for (const entry of [...this.active])
        this.callSafely(entry, 'fixed update', () => entry.simulation.fixedUpdate(update));
    }
    const frame = {
      timeSeconds: this.timeSeconds,
      interpolationAlpha: this.accumulator / this.fixedStepSeconds,
    };
    for (const entry of [...this.active])
      this.callSafely(entry, 'render update', () => entry.simulation.renderUpdate(frame));
  }

  async invalidate(
    snapshot: MobySimulationProjectSnapshot,
    invalidation: MobySimulationInvalidation,
  ): Promise<void> {
    this.ensureUsable();
    if (this.snapshot?.projectId !== snapshot.projectId) {
      await this.setSnapshot(snapshot);
      return;
    }
    this.snapshot = snapshot;
    if (!this.enabled || !this.contextAvailable) return;
    const recreate = new Set<number>();
    const activeClasses = new Set(this.active.map((entry) => entry.classId));
    for (const entry of [...this.active]) {
      const entities = entitiesForClass(snapshot, entry.classId);
      if (!sameEntityIds(entities.map((entity) => entity.id), entry.simulation.entityIds)) {
        recreate.add(entry.classId);
        continue;
      }
      const context = { snapshot, entities, services: this.services };
      this.callSafely(entry, 'invalidation', () => {
        if (entry.simulation.invalidate(context, invalidation) === 'recreate') recreate.add(entry.classId);
      });
    }
    for (const entity of snapshot.entities) {
      if (invalidation.entityIds.includes(entity.id) && !activeClasses.has(entity.classId)
        && this.registry.resolve(snapshot.game, entity.classId)) recreate.add(entity.classId);
    }
    if (recreate.size > 0) await this.recreateClasses(recreate);
  }

  contextLost(): void {
    this.ensureUsable();
    if (!this.contextAvailable) return;
    this.contextAvailable = false;
    this.generation += 1;
    this.clearActive();
    this.resetClock();
  }

  async contextRestored(): Promise<void> {
    this.ensureUsable();
    if (this.contextAvailable) return;
    this.contextAvailable = true;
    if (this.enabled && this.snapshot) await this.createActive(this.snapshot);
  }

  getStats(): MobySimulationStats {
    return {
      registeredFactories: this.registry.size,
      activeSimulators: this.active.length,
      simulatedEntities: this.active.reduce((total, entry) => total + entry.simulation.entityIds.length, 0),
      fixedTicks: this.fixedTicks,
      droppedSeconds: this.droppedSeconds,
      hiddenProxies: this.active.reduce((total, entry) => total + entry.hiddenEntityIds.length, 0),
      selectionBindings: this.active.reduce((total, entry) => total + entry.unbindSelection.length, 0),
    };
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.enabled = false;
    this.generation += 1;
    this.clearActive();
    this.snapshot = undefined;
    this.resetClock();
  }

  private async recreateAll(): Promise<void> {
    this.generation += 1;
    this.clearActive();
    if (this.snapshot) await this.createActive(this.snapshot);
  }

  private async recreateClasses(classIds: ReadonlySet<number>): Promise<void> {
    for (const entry of [...this.active]) {
      if (classIds.has(entry.classId)) this.release(entry);
    }
    if (this.snapshot) await this.createActive(this.snapshot, classIds);
  }

  private async createActive(
    snapshot: MobySimulationProjectSnapshot,
    onlyClasses?: ReadonlySet<number>,
  ): Promise<void> {
    const generation = ++this.generation;
    const byClass = new Map<number, typeof snapshot.entities>();
    for (const entity of snapshot.entities) {
      if (onlyClasses && !onlyClasses.has(entity.classId)) continue;
      if (!this.registry.resolve(snapshot.game, entity.classId)) continue;
      const entities = byClass.get(entity.classId);
      if (entities) byClass.set(entity.classId, [...entities, entity]);
      else byClass.set(entity.classId, [entity]);
    }
    await Promise.all([...byClass].map(async ([classId, entities]) => {
      const factory = this.registry.resolve(snapshot.game, classId)!;
      const context: MobySimulationFactoryContext = { snapshot, entities, services: this.services };
      let simulation: MobySimulation | null;
      try {
        simulation = await factory(context);
      } catch (cause) {
        this.report(classId, entities.map((entity) => entity.id), 'create', cause);
        return;
      }
      if (!simulation) {
        this.services.reportDiagnostic({
          code: 'MOBY_SIM_UNSUPPORTED_DATA',
          severity: 'warning',
          entityIds: entities.map((entity) => entity.id),
          message: `Class 0x${hexClass(classId)} simulation could not use the available data; ordinary proxies remain visible.`,
        });
        return;
      }
      if (this.disposed || !this.enabled || !this.contextAvailable || generation !== this.generation) {
        disposeSimulation(simulation);
        return;
      }
      const entry: ActiveSimulation = {
        classId,
        simulation,
        unbindSelection: [],
        hiddenEntityIds: [],
      };
      try {
        simulation.enable();
        for (const entityId of simulation.entityIds) {
          this.services.setProxyVisible(entityId, false);
          entry.hiddenEntityIds.push(entityId);
        }
        for (const binding of simulation.selectionObjects ?? [])
          entry.unbindSelection.push(this.services.bindSelection(binding.object, binding.entityId));
        this.active.push(entry);
      } catch (cause) {
        this.release(entry, false);
        this.report(classId, simulation.entityIds, 'enable', cause);
      }
    }));
  }

  private callSafely(entry: ActiveSimulation, operation: string, callback: () => void): void {
    if (!this.active.includes(entry)) return;
    try {
      callback();
    } catch (cause) {
      this.release(entry);
      this.report(entry.classId, entry.simulation.entityIds, operation, cause);
    }
  }

  private report(classId: number, entityIds: readonly string[], operation: string, cause: unknown): void {
    const detail = cause instanceof Error ? cause.message : String(cause);
    const diagnostic: MobySimulationDiagnostic = {
      code: 'MOBY_SIM_FAILED',
      severity: 'error',
      entityIds,
      message: `Class 0x${hexClass(classId)} simulation failed during ${operation}: ${detail}. Ordinary proxies remain visible.`,
    };
    this.services.reportDiagnostic(diagnostic);
  }

  private clearActive(): void {
    for (const entry of [...this.active]) this.release(entry);
    this.active.length = 0;
  }

  private release(entry: ActiveSimulation, remove = true): void {
    if (remove) {
      const index = this.active.indexOf(entry);
      if (index >= 0) this.active.splice(index, 1);
    }
    for (const unbind of entry.unbindSelection.splice(0)) {
      try { unbind(); } catch { /* selection cleanup is best effort */ }
    }
    for (const entityId of entry.hiddenEntityIds.splice(0))
      this.services.setProxyVisible(entityId, true);
    disposeSimulation(entry.simulation);
  }

  private resetClock(): void {
    this.accumulator = 0;
    this.timeSeconds = 0;
    this.fixedTicks = 0;
    this.droppedSeconds = 0;
  }

  private ensureUsable(): void {
    if (this.disposed) throw new Error('Moby simulation controller is disposed.');
  }
}

function entitiesForClass(snapshot: MobySimulationProjectSnapshot, classId: number) {
  return snapshot.entities.filter((entity) => entity.classId === classId);
}

function disposeSimulation(simulation: MobySimulation): void {
  try { simulation.disable(); } catch { /* continue releasing owned resources */ }
  try { simulation.dispose(); } catch { /* a broken simulator must not break the viewport */ }
}

function hexClass(classId: number): string {
  return classId.toString(16).padStart(4, '0').toUpperCase();
}

function sameEntityIds(left: readonly string[], right: readonly string[]): boolean {
  if (left.length !== right.length) return false;
  const expected = new Set(left);
  return right.every((value) => expected.has(value));
}
