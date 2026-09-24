# horizon-forge-v2
v2 version of horizon forge, completely different architecture not based in unity.

## Development

Requires Node.js 22+, npm, and the .NET 10 SDK.

```sh
npm install
bash scripts/bootstrap-ratchet-sdk.sh
npm run dev
```

For an existing checkout at the pinned revision, use
`bash scripts/bootstrap-ratchet-sdk.sh --source /path/to/ratchet-ps2-cli`.
The `ratchet-sdk.version` selector accepts either an exact commit SHA or a release
tag such as `v0.4.4`. Publish and tag ratchet-ps2-cli first, update that file to
the new tag, then build or publish Forge.

`npm run build` verifies the Electron/React application and the .NET host.
`npm test` runs the protocol, identity/schema, host lifecycle, and desktop security checks.
`npm run package:linux` creates a self-contained Linux x64 app in `artifacts/`.
Pull requests and pushes run the locked Linux/Windows build matrix in GitHub Actions.

## Project planning

- [Product and technical specification](docs/forge-v2-spec.md)
- [Binary bridge protocol v1](docs/bridge-protocol-v1.md)
- [Identity and project schema v0](docs/identity-and-schema-v0.md)
- [UI guidelines](docs/ui-guidelines.md)
- [Milestones and task registers](docs/planning/README.md)
