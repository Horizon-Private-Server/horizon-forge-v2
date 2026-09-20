import { pathToFileURL } from 'node:url';

export function createNightlyVersion(base, runNumber, commit) {
  if (!/^\d+\.\d+\.\d+$/.test(base ?? '')) throw new Error('Base version must be MAJOR.MINOR.PATCH');
  if (!/^[1-9]\d*$/.test(runNumber ?? '')) throw new Error('Run number must be a positive integer');
  if (!/^[0-9a-f]{8,40}$/i.test(commit ?? '')) throw new Error('Commit must be an 8–40 character hexadecimal SHA');

  return `${base}-nightly.${runNumber}.${commit.slice(0, 8).toLowerCase()}`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.stdout.write(createNightlyVersion(...process.argv.slice(2)));
}
