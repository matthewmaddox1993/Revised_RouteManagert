# Changelog

All notable changes to Revised DV Route are recorded here.

## 1.0.1 - 2026-10-10

### ADS-aware alternate routing

- Kept Advanced Dispatcher System optional: Revised DV Route detects whether ADS is installed and enabled without making it a required dependency.
- Added read-only checks of ADS's live track occupancy, route locks, and signal reservation/stop state, including when the player has not created a separate route in ADS.
- Limited ADS conflict handling to tracks used by the proposed route, preventing unrelated ADS warnings from rejecting a safe route.
- Ignored the player's own consist occupancy at the route origin and a matching route's own lock, preventing false conflicts.
- Added support for matching ADS `SIGNAL_STOP`/`SIGNAL_OFF` warnings and live ADS signal-protected tracks as route blockers.
- Retained matching interrupted or failed ADS route records for five minutes so recent reservation-loss warnings can still affect routing.
- Added one automatic retry that excludes the blocked rails reported by ADS.
- Verified that the retry is a genuinely different path and reports its first point of divergence as `ADS ALTERNATE CONFIRMED`.
- Prevented the original blocked path from becoming active when no different route can avoid the conflict.
- Rejected non-reroutable occupancy or route-lock conflicts at the requested origin or destination instead of accepting a cosmetically different path with the same blocked endpoint.
- Honored protected ADS switch locks during initial alignment and live re-alignment without changing or releasing ADS state.
- Preserved the completed route-specific ADS result so a later status refresh cannot replace it with a misleading unavailable message.
- Applied the same ADS safety check and alternate search to route flips and post-coupling freight-haul destination legs.
- Displayed the most recent ADS read-only safety status in the F10 compatibility panel.
- Clarified Comms Radio results: `ADS CHECK` means the route passed, `ADS WARNING` describes a conflict, and `ADS check unavailable` is reserved for an actual integration/API problem.
- Fixed unnecessarily long routes when ADS is installed: a signal reservation or stop on the origin or destination track (for example the train waiting at its own signal) no longer forces the alternate search to exclude the adjacent rail.
- Signal-level or route-level ADS conflicts no longer replace a direct route with an alternate that is both more than twice as long and over 750 m longer; the direct route is kept and the ADS warning is shown.
- Replaced the flat 5000-cost penalty for DV Signals/AI Traffic reserved tracks with a bounded per-track penalty, so reservations steer around busy blocks without forcing the longest loop.
- Stopped the path search from expanding states that cannot beat the best route already found.

### Signal-aware routing

- Routes strongly prefer tracks that are not reserved through DV Signals while retaining an unavoidable single-corridor route as a last resort.
- Protected junctions are never changed when their DV Signals reservation belongs to another train or signal.
- Active player routes reserve the next governing DV Signals block, retry while it is unavailable, and release it only after the entire train clears it.
- Exposed the player's rolling DV Signals reservation to compatible AI Traffic so it can honor player right-of-way while retaining its physical-safety and approach-lock protections.
- Delegated player signal authority to ADS when its DV Signals adapter is active, preventing competing writers from repeatedly alternating a signal between `CLEAR` and `RESTRICTED`.
- Throttled retries after an externally released reservation so an unexpected integration conflict cannot create a per-frame signal update loop.
- Fixed continuous switch and signal flapping during route generation and AI interactions by ensuring looping or reversing junctions are only aligned for their first crossing.

### Build and packaging

- Updated the mod manifest and assembly/file versions to `1.0.1`.
- Restored automatic deployment of the `Test` configuration to `Derail Valley\Mods\RevisedDVRoute` after a successful build.
- Kept Test, Debug, and Release output directories separate.
- Made the WAV files first-class build content and package the copies from the build output.
- Named the Release archive `RevisedDVRoute-1.0.1.zip`, deriving its version from `Info.json` during the build.
- Updated the Unity Mod Manager repository metadata for the 1.0.1 release.
- Expanded README compatibility, troubleshooting, build, and ADS-status documentation.

### Stability

- Guarded unsuccessful initial and ADS-alternate searches so a null route remains a clear command error instead of becoming a null-reference failure.
- Kept the DoubleTrack compatibility monitor alive while the rail registry is unavailable during game startup.
- Preserved wrong-heading detection while re-aligning switches ahead of a stopped or moving train.
