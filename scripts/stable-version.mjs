import { pathToFileURL } from 'node:url';

export function validateStableVersion(packageVersion, tag) {
  if (!/^\d+\.\d+\.\d+$/.test(packageVersion ?? ''))
    throw new Error('Package version must be MAJOR.MINOR.PATCH');
  if (tag !== `v${packageVersion}`)
    throw new Error(`Stable tag ${tag} does not match package version ${packageVersion}`);
  return packageVersion;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  process.stdout.write(validateStableVersion(process.argv[2], process.argv[3]));
