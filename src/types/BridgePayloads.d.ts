export interface EchoRequest {
  message: string;
  delayMs: number;
}

export interface DevelopmentIsoRequest {
  sourcePath: string;
  targetPath: string;
  fingerprint: string;
  overwrite: boolean;
}
