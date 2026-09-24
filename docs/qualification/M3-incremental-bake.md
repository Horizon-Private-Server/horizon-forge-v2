# M3 incremental bake qualification

Date: 2026-09-22  
Target: UYA NTSC-U 1.00 (`uya-ntsc-u`)  
Command: `npm test`

The synthetic end-to-end fixture creates a base-level project, bakes all ten
declared layers, confirms a no-op bake writes nothing, edits a tie and confirms
only ties and lighting rebuild, cancels after a shrub write, injects missing
lighting data after a tie write, repairs from the verified source, and retries.

Result: passed. Cancellation and injected failure restored the prior active
manifest; repeated unchanged output preserved identical manifest bytes. The full
TypeScript, C#, host integration, renderer, security, and desktop build suite also
passed.
