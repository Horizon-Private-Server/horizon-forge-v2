# Asset repair and catalog maintenance v0

## Missing-asset diagnosis and repair

Project inspection groups unresolved entity references by Asset ID. Each diagnostic
contains the expected kind, affected entity count, portable entity/catalog
provenance, and whether the asset can be regenerated from the clean UYA source.
Project-owned assets cannot be regenerated from the retail disc and must be
restored from a project backup.

Repair first validates the selected ISO against the source fingerprint stored in
the project. It then rescans only the source levels referenced by repairable
missing assets and commits their assets through the content-addressed catalog.
Selecting a moved copy of the same clean ISO in Setup updates its machine-local
path without invalidating the development ISO or completed import record.

Base instances omitted when a partial project was originally created have no
Asset ID or entity record to repair. Forge identifies these separately and directs
the user to re-import the catalog and recreate the project.

## Explicit garbage collection

Catalog maintenance scans the configured projects directory and recent projects,
including recovery snapshots. Attached asset IDs and their parent IDs are treated
as references. Reparse-point directories are not traversed.

A preview reports the inspected project count, protected catalog entries,
candidate count and bytes, and candidate counts by kind. Any unavailable or
invalid known project blocks deletion. Applying cleanup requires the exact
SHA-256 confirmation token from the preview; a changed catalog or reference set
invalidates the token and requires a new preview.

Cleanup first commits a catalog without the unreferenced entries, then deletes
their blobs. Interruption can therefore leave only safe, unindexed orphan blobs,
which a later preview can collect. Invalidly named files in the blob tree are left
untouched. Removing cataloged vanilla assets clears the setup import-completion
marker, and the importer verifies every completed checkpoint's blobs before reuse.

## Portability

Projects continue to store only Asset IDs and portable relative paths. Moving a
project to another supported OS or user profile resolves its vanilla references
when that user has imported matching canonical assets. Authored tests cover moved
project resolution against a separately built matching catalog.
