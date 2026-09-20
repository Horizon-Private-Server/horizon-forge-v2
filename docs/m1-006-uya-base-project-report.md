# M1-006 representative UYA base-project run

Date: 2026-09-20  
Host SDK revision: `0deb8869f7f080a7afd4543a663e20c02c344da8`  
Source: verified NTSC-U UYA revision 1.00 (`ba9f2b38c7346e7b6e5b8e87717d5893`)  
Base: level 3  
Catalog: completed UYA v1 global import

The host discovered 51 populated level entries. Level 3 preflight found 422 moby
instances: 378 use renderable models backed by global Asset IDs and 44 are
intentional model-less/controller classes. No renderable instance was missing a
catalog asset. Those counts and the current tie/shrub capability gap were reported
before the project directory was selected or written.

Creation wrote all 422 stable project entities with source-instance provenance and
no copied vanilla blobs. Model-less entities intentionally have no Asset ID.
Reopening retained all entities with a missing-asset count of zero. The authored
lifecycle test additionally verifies rename, project-only
entity deletion, global-blob isolation, preflight refusal before writes, and stable
Entity IDs across reopen.
