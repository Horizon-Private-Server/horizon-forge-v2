import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';

import type { DevelopmentIsoResult, ForgeHostStatus, Progress, UyaIsoIdentity } from '../../types/ForgeApi.js';
import {
  BridgeFrameDecoder,
  decodeErrorMessage,
  encodeFrame,
} from './FrameCodec.js';
import type { BridgeFrame, HostRequest } from './BridgeProtocol.js';
import { BridgeErrorCode, BridgeMessageKind, BridgeOpcode, BridgeProtocolError } from './BridgeProtocol.js';
import {
  decodeHandshake,
  decodeDevelopmentIso,
  decodeProgress,
  decodeText,
  decodeUyaIsoValidation,
  encodeDevelopmentIsoRequest,
  encodeEchoRequest,
  encodeText,
} from './PayloadCodec.js';

interface PendingRequest {
  opcode: BridgeOpcode;
  resolve: (payload: Buffer) => void;
  reject: (error: Error) => void;
  onProgress?: (progress: Progress) => void;
}

interface ReadyWaiter {
  promise: Promise<ForgeHostStatus>;
  resolve: (handshake: ForgeHostStatus) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
}

export class HostOperationError extends Error {
  constructor(readonly code: BridgeErrorCode, message: string) {
    super(message);
    this.name = 'HostOperationError';
  }
}

export class HostClient {
  readonly #command: string;
  readonly #args: string[];
  readonly #restartDelayMs: number;
  #child: ChildProcessWithoutNullStreams | undefined;
  #decoder = new BridgeFrameDecoder();
  #handshake: ForgeHostStatus | undefined;
  #ready: ReadyWaiter | undefined;
  #pending = new Map<number, PendingRequest>();
  #nextRequestId = 1;
  #writeQueue: Promise<void> = Promise.resolve();
  #restartTimer: NodeJS.Timeout | undefined;
  #shouldRun = false;

  constructor(command: string, args: string[], restartDelayMs = 250) {
    this.#command = command;
    this.#args = args;
    this.#restartDelayMs = restartDelayMs;
  }

  get processId(): number | undefined {
    return this.#child?.pid;
  }

  async start(): Promise<ForgeHostStatus> {
    this.#shouldRun = true;
    if (this.#handshake) return this.#handshake;
    if (this.#ready) return this.#ready.promise;
    if (this.#restartTimer) {
      clearTimeout(this.#restartTimer);
      this.#restartTimer = undefined;
    }

    const child = spawn(this.#command, this.#args, {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });
    this.#child = child;
    this.#decoder = new BridgeFrameDecoder();

    let resolve!: (handshake: ForgeHostStatus) => void;
    let reject!: (error: Error) => void;
    const promise = new Promise<ForgeHostStatus>((resolvePromise, rejectPromise) => {
      resolve = resolvePromise;
      reject = rejectPromise;
    });
    const timer = setTimeout(() => {
      this.#fail(child, new Error('Forge host handshake timed out'));
    }, 10_000);
    this.#ready = { promise, resolve, reject, timer };

    child.stdout.on('data', (chunk: Buffer) => {
      try {
        for (const frame of this.#decoder.push(chunk)) this.#dispatch(frame);
      } catch (error) {
        this.#fail(child, error instanceof Error ? error : new Error(String(error)));
      }
    });
    child.stdout.on('end', () => {
      try {
        this.#decoder.complete();
      } catch (error) {
        this.#fail(child, error instanceof Error ? error : new Error(String(error)));
      }
    });
    child.stderr.on('data', (chunk: Buffer) => process.stderr.write(`[Forge.Host] ${chunk.toString()}`));
    child.stdin.on('error', () => {});
    child.once('error', (error) => this.#fail(child, error));
    child.once('exit', (code, signal) => {
      this.#fail(child, new Error(`Forge host exited (${signal ?? code ?? 'unknown'})`));
    });

    return promise;
  }

  async stop(): Promise<void> {
    this.#shouldRun = false;
    if (this.#restartTimer) clearTimeout(this.#restartTimer);
    this.#restartTimer = undefined;
    const child = this.#child;
    if (!child) return;

    await new Promise<void>((resolve) => {
      const timeout = setTimeout(() => child.kill('SIGKILL'), 2_000);
      child.once('exit', () => {
        clearTimeout(timeout);
        resolve();
      });
      child.kill();
    });
  }

  async echo(
    message: string,
    delayMs = 0,
    onProgress?: (progress: Progress) => void,
  ): Promise<HostRequest<string>> {
    const request = await this.#request(BridgeOpcode.Echo, encodeEchoRequest(message, delayMs), onProgress);
    return { requestId: request.requestId, result: request.result.then(decodeText) };
  }

  async validateUyaIso(
    path: string,
    onProgress?: (progress: Progress) => void,
  ): Promise<HostRequest<UyaIsoIdentity>> {
    const request = await this.#request(BridgeOpcode.ValidateUyaIso, encodeText(path), onProgress);
    return { requestId: request.requestId, result: request.result.then(decodeUyaIsoValidation) };
  }

  async createDevelopmentIso(
    sourcePath: string,
    targetPath: string,
    fingerprint: string,
    overwrite: boolean,
    onProgress?: (progress: Progress) => void,
  ): Promise<HostRequest<DevelopmentIsoResult>> {
    const request = await this.#request(BridgeOpcode.CreateDevelopmentIso, encodeDevelopmentIsoRequest({
      sourcePath, targetPath, fingerprint, overwrite,
    }), onProgress);
    return { requestId: request.requestId, result: request.result.then(decodeDevelopmentIso) };
  }

  async cancel(requestId: number): Promise<void> {
    const pending = this.#pending.get(requestId);
    if (!pending) return;
    await this.#write({
      kind: BridgeMessageKind.Cancel,
      opcode: pending.opcode,
      status: BridgeErrorCode.None,
      requestId,
      payload: Buffer.alloc(0),
    });
  }

  async #request(
    opcode: BridgeOpcode,
    payload: Uint8Array,
    onProgress?: (progress: Progress) => void,
  ): Promise<HostRequest<Buffer>> {
    await this.start();
    const requestId = this.#allocateRequestId();
    let resolve!: (payload: Buffer) => void;
    let reject!: (error: Error) => void;
    const result = new Promise<Buffer>((resolvePromise, rejectPromise) => {
      resolve = resolvePromise;
      reject = rejectPromise;
    });
    this.#pending.set(requestId, { opcode, resolve, reject, onProgress });

    try {
      await this.#write({
        kind: BridgeMessageKind.Request,
        opcode,
        status: BridgeErrorCode.None,
        requestId,
        payload,
      });
    } catch (error) {
      this.#pending.delete(requestId);
      reject(error instanceof Error ? error : new Error(String(error)));
      throw error;
    }
    return { requestId, result };
  }

  #dispatch(frame: BridgeFrame): void {
    if (frame.kind === BridgeMessageKind.Handshake) {
      if (this.#handshake) throw new BridgeProtocolError(BridgeErrorCode.InvalidMessageKind, 'Duplicate host handshake');
      this.#handshake = decodeHandshake(frame.payload);
      if (this.#ready) {
        clearTimeout(this.#ready.timer);
        this.#ready.resolve(this.#handshake);
        this.#ready = undefined;
      }
      return;
    }

    const pending = this.#pending.get(frame.requestId);
    if (!pending) throw new BridgeProtocolError(BridgeErrorCode.InvalidRequestId, `Unknown response ID: ${frame.requestId}`);
    if (frame.opcode !== pending.opcode && frame.opcode !== BridgeOpcode.Control) {
      throw new BridgeProtocolError(BridgeErrorCode.UnknownOpcode, 'Response opcode does not match request');
    }

    switch (frame.kind) {
      case BridgeMessageKind.Progress:
        try {
          pending.onProgress?.(decodeProgress(frame.payload));
        } catch (error) {
          console.error('Forge host progress callback failed', error);
        }
        break;
      case BridgeMessageKind.Result:
        this.#pending.delete(frame.requestId);
        pending.resolve(Buffer.from(frame.payload));
        break;
      case BridgeMessageKind.Error:
        this.#pending.delete(frame.requestId);
        pending.reject(new HostOperationError(frame.status, decodeErrorMessage(frame.payload)));
        break;
      default:
        throw new BridgeProtocolError(BridgeErrorCode.InvalidMessageKind, `Unexpected host frame: ${frame.kind}`);
    }
  }

  #write(frame: BridgeFrame): Promise<void> {
    const child = this.#child;
    if (!child?.stdin.writable) return Promise.reject(new Error('Forge host input is unavailable'));
    const bytes = encodeFrame(frame);
    const write = this.#writeQueue.then(() => new Promise<void>((resolve, reject) => {
      if (child !== this.#child || !child.stdin.writable) {
        reject(new Error('Forge host restarted before write'));
        return;
      }
      child.stdin.write(bytes, (error) => error ? reject(error) : resolve());
    }));
    this.#writeQueue = write.catch(() => {});
    return write;
  }

  #allocateRequestId(): number {
    while (this.#pending.has(this.#nextRequestId) || this.#nextRequestId === 0) {
      this.#nextRequestId = (this.#nextRequestId + 1) >>> 0;
    }
    const value = this.#nextRequestId;
    this.#nextRequestId = (this.#nextRequestId + 1) >>> 0;
    return value;
  }

  #fail(child: ChildProcessWithoutNullStreams, error: Error): void {
    if (child !== this.#child) return;
    const wasReady = this.#handshake !== undefined;
    this.#child = undefined;
    this.#handshake = undefined;
    child.kill();

    if (this.#ready) {
      clearTimeout(this.#ready.timer);
      this.#ready.reject(error);
      this.#ready = undefined;
    }
    for (const pending of this.#pending.values()) pending.reject(error);
    this.#pending.clear();

    if (this.#shouldRun && wasReady && !this.#restartTimer) {
      this.#restartTimer = setTimeout(() => {
        this.#restartTimer = undefined;
        void this.start().catch((restartError) => console.error('Forge host restart failed', restartError));
      }, this.#restartDelayMs);
    }
  }
}
