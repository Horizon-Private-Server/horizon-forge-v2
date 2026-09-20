interface ForgeHostStatus {
  hostVersion: string;
  sdkRevision: string;
  supportedGames: string[];
  capabilities: string[];
}

interface Window {
  forge: {
    getHostStatus(): Promise<ForgeHostStatus>;
    getTfragUrl(): Promise<string | undefined>;
    echo(message: string): Promise<string>;
  };
}
