# Changelog

All notable changes to Revised DV Route are recorded here.

## 1.0.4 - 2026-10-15

### AI routing and traffic protection

- Changed route searches to use Dijkstra ordering for dynamic track costs and yield between batches on Unity's main thread, where rail and junction state can be read safely.
- Used live AI Traffic reservations while driving, rather than relying on the route planner's one-second snapshot when entering a block.
- Added AI Traffic's live bogie-distance and facing-signal checks so trains can brake for traffic on the current track and for STOP aspects before the next track boundary.
- Refactored locomotive AI speed control into a dedicated hazard/speed-profile pipeline, inspired by AI Traffic's driver design while retaining Revised DV Route's route, ADS, and DV Signals integrations.
- Made reverse-manoeuvre junction switching use the same exact branch selection and ADS protected-switch checks as initial route alignment, preventing compatibility gaps during route reversals.
- Treated live ADS occupied-track or signal warnings without a matching ADS route as advisory: route planning now tries one reroute, then keeps the direct route with a warning when no different path exists.
- Added a DV Signals STOP-aspect check at each facing signal's actual mast position, including when AI Traffic is not installed; reservations alone no longer stand in for signal aspects.
- Measured unrelated bogies ahead on the current track without treating trains behind the AI as immediate obstacles. Kept the selected stationary consist exempt during a coupling approach while other trains remain blockers.
- Prevented automatic switch changes when AI Traffic protects a junction or DV Signals reservation status cannot be read.
- Made the AI hold when its route position or an active signal/traffic safety check is unavailable, and added track, speed, target, and next-track distance to hold logs.

### Speed control

- Applied braking envelopes for upcoming speed-limit drops, occupied tracks, reservations, and signal stops, allowing for brake-pipe response and a stopping margin.
- Blended dynamic and train brakes during overspeed and stops, using air brakes to complete low-speed stops and releasing brakes before applying traction.

## 1.0.3 - 2026-10-15

### ADS and DV Signals interoperability

- Delegated signal authority only when ADS has an active, reserved route for the same consist, path, and native DV Signals head; merely loading the ADS signal adapter no longer disables Revised DV Route's rolling reservation.
- Used DV Signals' host-authoritative request and release callbacks for player route reservations while the Multiplayer mod is active.
- Treated the matching ADS-owned DV Signals reservation as the active route's authority instead of a foreign blocker.
- Checked ADS conflicts with ADS track IDs, refreshed live route safety while locomotive automation is running, and included complete reserved signal-block footprints.
- Preserved both protected and normal ADS route switch plans during automatic alignment, including explicit branch selection for multi-branch junctions.

### Locomotive AI and direction handling

- Reworked departure routing to use the consist's physical outer bogies instead of assuming `Bogies[0]` is the leading end.
- Determined the forward end from the controlled locomotive's orientation and correctly accounted for front-versus-rear bogie track direction.
- Removed the previous reverser position from route selection so a stale or neutral handle can no longer make the route start behind the train.
- Selected the required reverser direction from the route's first transition, including routes which genuinely require backing out.
- Changed the AI departure sequence to close the throttle, stop and secure the train if necessary, select the exact numeric reverser position, and only then release the brakes.
- Replaced repeated blind reverser inputs with verified movement to the requested reverser position.
- Restricted reverser changes to explicit `ReverseTrain` manoeuvres; a gravity-induced `WrongHeading` state now arrests rollback and reapplies power in the already selected direction instead of flipping ends.
- Added a one-reversal-per-manoeuvre latch and only re-arm it after confirmed movement in the selected direction, so downhill rollback cannot cycle the reverser between forward and reverse.
- Measured AI control speed in the selected reverser direction so backward acceleration no longer looks like successful forward acceleration and suppresses traction.
- Added low-speed power headroom for stalled trains on steep grades while retaining throttle ramping, sanding, amperage, wheel-slip, and thermal protections.

### Signal stability

- Stopped reclaiming a DV Signals reservation every two seconds after DV Signals invalidates it. Local reservation writes are suspended for the remainder of that route while foreign reservations continue to be respected.
- Prevented the resulting reservation/aspect feedback loop from repeatedly recalculating the signal network and flooding `Player.log`.
- Debounced locomotive-AI signal-clear messages for three seconds while applying the actual stop or resume command immediately.
- Kept initial unavailable-block retries, normal block advancement, ADS authority delegation, and multiplayer host authority intact.

### Gameplay performance

- Cached expensive ADS signal-authority matching for 500 ms instead of rebuilding and comparing the complete reflected route state on every tracker update.
- Reduced release RouteTracker polling from 10 Hz to 5 Hz; this remains faster than the locomotive AI's control cycle while halving endpoint, signal, and reservation polling work.
- Kept live switch repair bounded by distance and time so long routes are not rescanned every frame.
- Cached calculated track speed limits for locomotive automation and retained background route searches.

## 1.0.1a - 2026-10-14

### Hotfix

- Stopped rebuilding the complete DoubleTrack layout cache every five seconds when the loaded layout has not changed.
- Reduced the one-time DoubleTrack lane-pairing scan by grouping nearby track sections spatially.
- Kept fast layout-reload detection so the cache is rebuilt when the world or selected DoubleTrack mode changes.

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
