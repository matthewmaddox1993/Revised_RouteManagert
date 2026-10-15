# Revised DV Route

Revised DV Route is a route-management mod for **Derail Valley build 99.7**. It adds route planning, automatic junction alignment, route tracking, cruise control, and experimental locomotive automation to the Comms Radio. Optional integrations support DV Signals interlocking, AI Traffic right-of-way, and both DoubleTrack layouts.

> **Version:** 1.0.4<br>
> **Status:** Route planning and switching are ready for normal use. Cruise control, locomotive AI, and automated freight hauling remain experimental and should be supervised.

See [CHANGELOG.md](CHANGELOG.md) for the complete update notes.

## Original author credit

Revised DV Route is based on the original **DV Route Manager** created by **Wally (WallyCZ)**. Wally's original design and implementation established the route-planning, route-tracking, automatic-switching, and train-control foundation on which this revised release is built. Full credit and thanks go to Wally for creating the original project and making this continued development possible.

## Highlights

- Plans routes with A* pathfinding, including turntables and necessary reversals.
- Aligns junctions for the selected route and tracks progress to the destination.
- Provides route information, train information, route reversal, a train-end alarm, and route clearing through the Comms Radio.
- Includes PID-based cruise control for supported locomotives, including DM3 and steam locomotives.
- Includes experimental locomotive AI and freight-haul automation with route-aware direction selection, signal/occupancy braking, speed-limit lookahead, and safe reverser handling.
- Respects existing DV Signals and AI Traffic reservations and maintains a rolling player-route block reservation when ADS is not managing signal synchronization.
- Exposes the reserved player route to AI Traffic through DV Signals so conflicting AI trains can yield.
- Routes appropriately for the DoubleTrack layout actually loaded in the current world and excludes its abandoned and deliberate derailment tracks.
- Shows dependency and compatibility status directly in Unity Mod Manager's F10 panel.

## Requirements

| Component | Requirement | Purpose |
|---|---|---|
| Derail Valley | Build 99.7 | Supported game version. |
| Unity Mod Manager | 0.27.3 or newer | Loads and configures the mod. |
| [CommsRadioAPI](https://www.nexusmods.com/derailvalley/mods/813) | 1.0.3 | **Required.** Adds Revised DV Route to the Comms Radio. |
| .NET Framework 4.8 Developer Pack | Source builds only | Required to compile the project. |

## Installation

1. Install Unity Mod Manager 0.27.3 or newer for Derail Valley build 99.7.
2. Install and enable CommsRadioAPI 1.0.3.
3. Extract the release archive into `Derail Valley\Mods\RevisedDVRoute\`.

   `Info.json`, `RevisedDVRoute.dll`, `PriorityQueue.dll`, and the `audio` folder must be directly inside that folder. Do not leave them inside an additional nested archive folder.

4. Start Derail Valley and press **F10**. Confirm that both CommsRadioAPI and Revised DV Route are enabled.

## Using Revised DV Route

Open the Comms Radio, select the **Revised DV Route** mode, then choose an action from the main menu.

### New route

Create a route using one of these starting points:

- The last locomotive used to the selected job's destination.
- The last locomotive used to a specific track.
- The cars assigned to a selected job and that job's destination.

After a route is found, Revised DV Route aligns the required junctions where it is safe to do so. The route summary reports length, initial heading, locations travelled through, and required reversals.

When DV Signals route reservation is enabled, the mod requests the next signal block on the active route. It retries while the initial block is occupied, releases an owned block only after the rear of the train clears it, and then requests the next one. If DV Signals later invalidates an owned reservation because another integration changed its block or interlocking state, Revised DV Route suspends further local reservation writes for that route instead of fighting the signal system. A junction protected by another reservation is left unchanged. If ADS has an active reserved route for the same consist and path, Revised DV Route delegates signal authority to that route and does not create a competing rolling reservation. Loading ADS by itself does not disable the player-route reservation.

### Active route

An active route provides:

- Live route-tracker status and remaining distance.
- Train length, car count, and weight.
- Opposite-heading route changes when the loaded rail graph provides another path from the same origin.
- A train-end alarm.
- Clearing the current route.

### Map route colors

The green and red marks on the map are route-direction markers placed approximately every 200 metres. Green shows the initial movement direction. The color changes to red at the first planned reversal and alternates after each additional reversal. They are not live signal aspects or block-occupancy indicators.

### Cruise control and locomotive AI

Cruise control can hold the current speed, 30 km/h, 60 km/h, or an adjusted target speed. Locomotive AI can drive a boarded locomotive to a selected destination and can perform freight-haul tasks for supported jobs.

Automatic routing starts from the physical end of the consist lying ahead of the controlled locomotive. The AI derives forward or reverse from the route's first transition rather than from the previous reverser position. It closes the throttle and secures the train before changing direction, verifies the requested reverser position, and releases the brakes only after direction is established. A reversal is issued only for an explicit planned manoeuvre and is re-armed only after the train moves in the selected direction. If gravity rolls the consist backward on a grade, the AI brakes the rollback and builds power in the existing direction instead of flipping the reverser.

While driving, the AI considers locomotive and occupied-track speed limits, upcoming lower limits, foreign occupancy, DV Signals/ADS restrictions, rotating turntables, planned route reversals, and destination approach conditions. These protections remain experimental; the player should continue supervising the train.

These systems interact with live traffic, signals, grades, braking, and locomotive controls. Keep control of the train while testing them, particularly with long consists, steam locomotives, or unfamiliar routes.

## Compatibility

All integrations below are optional unless marked required. Revised DV Route does not require their assemblies to be installed; it enables the relevant behavior only when the compatible mod is active.

| Mod | Status | Revised DV Route behavior |
|---|---|---|
| CommsRadioAPI | Required | Supplies the Comms Radio mode and controls. |
| [DoubleTrack](https://github.com/Chump-the-Lump/DV-DoubleTrack) | Optional | Detects the physical layout currently loaded by DoubleTrack. Normal layout routes prefer right-hand-running paired lanes and avoid unnecessary crossovers. Hard layout routes use its loaded topology without Normal-layout lane assumptions. Tracks tagged by DoubleTrack as `Abandoned` or `Derail-*` are never selected. |
| DV Signals | Optional | Strongly prefers unreserved blocks, retains reserved single-corridor tracks as a last-resort route, and never changes a junction protected by a foreign reservation. For the active player route it reserves one governing signal block at a time and releases each block after the complete train clears it, unless signal authority is delegated to ADS. If DV Signals invalidates the reservation, local writes are suspended for that route to prevent an aspect-update feedback loop. |
| [AI Traffic](https://github.com/Killermops27/dv-ai-traffic) | Optional; requires DV Signals for player right-of-way | Reads AI Traffic's active track reservations before each route search and strongly prefers an unreserved path. AI Traffic recognizes Revised DV Route's non-AI DV Signals reservation as player right-of-way. Conflicting AI trains keep their physical-safety and approach-locking protections while yielding when safe. Enable **Player Priority** in AI Traffic for player-first dispatching behavior. |
| DriverAssist | Optional | Includes compatibility safeguards so known job-registration and unsupported-locomotive conditions do not abort game loading. |
| Revised_Mph | Optional | Loads before Revised DV Route and uses the MPH value displayed by converted speed signs when calculating AI speed targets. Runtime enable/disable changes are detected automatically. |
| Advanced Dispatcher System | Optional, read-only ADS integration | Checks matching ADS route warnings and ADS's live route locks, occupancy, complete DV Signals block footprints, and reserved/stop-signal protection, even when no separate ADS route plan exists. A blocking track or governing stop signal causes one additional search with affected rails excluded. Protected and normal ADS routes are preserved during automatic switch alignment. ADS becomes the sole DV Signals authority only when it has accepted a matching reserved route for the same consist and path; otherwise Revised DV Route keeps its own rolling reservation. Revised DV Route does not publish routes to ADS or release ADS-owned state. |

### DoubleTrack Normal and Hard layouts

DoubleTrack's selected setting and the rails in the current world can temporarily differ. Revised DV Route deliberately routes according to the **loaded** rails, not only the value saved in `Settings.xml`.

Hard mode represents removed corridors with connected `Abandoned` RailTrack objects and also adds deliberate `Derail-*` spurs. Revised DV Route explicitly blocks both categories; it does not assume that every connected RailTrack is safe merely because it remains in the live rail graph.

When changing DoubleTrack between Normal and Hard:

1. Change the mode in DoubleTrack and save its settings.
2. Return to the main menu and load the world again, or restart the game.
3. Create a new route after the railway scene has loaded.

The F10 compatibility panel can report, for example, `Hard layout active` or `Normal layout active; Hard selected - reload the world to apply`. The second message means the XML setting is saved, but the live rails are still the earlier layout. This is expected until DoubleTrack reloads its world layout.

## F10 settings and compatibility panel

Select Revised DV Route in Unity Mod Manager and press **F10** to access settings and the **Compatible mods** panel.

The panel reports each supported mod as:

- `Installed and enabled`
- `Installed, disabled`
- `Not installed`

It also supplies integration-specific detail:

- DoubleTrack's selected and active layout state.
- Whether DV Signals protection and the rolling player reservation are enabled, delegated to ADS, currently holding a signal block, or suspended after DV Signals released it.
- Whether AI Traffic reservations are considered and its Player Priority option is enabled.
- Which dependency is required versus optional.

Available settings are:

| Setting | Default | Description |
|---|---:|---|
| Reversing strategy | OnlyIfNeeded | Controls how the route planner considers reversals. It can also be changed through the Comms Radio settings page. |
| Respect DV Signals reservations | Enabled | Strongly discourages foreign-reserved blocks and prevents automatic changes to protected junctions. Disable only if you intentionally want standard routing behavior. |
| Reserve next DV Signals block for active route | Enabled | Claims one rolling player-route block through DV Signals, allowing signal aspects and compatible AI traffic to honor the selected route. Multiplayer requests use DV Signals' host-authoritative reservation API. This write path is delegated only when ADS has accepted a matching reserved route. An externally invalidated reservation is not repeatedly reclaimed on the same route. |
| Train-end alarm key | `N` | Notifies the active route tracker that the train end has passed. |

## Command Terminal

For advanced use, Revised DV Route registers the `route` command in Derail Valley's Command Terminal. Enter `route` with no arguments to display the in-game command examples. Common commands include:

```text
route job [jobname]
route loco [jobname]
route from loco to [track]
route opposite
route info
route clear
route auto [track]
route auto stop
```

## Troubleshooting

| Symptom | What to check |
|---|---|
| Revised DV Route is missing from the radio | Confirm CommsRadioAPI is installed and enabled, then restart the game after installing the mod. |
| F10 reports CommsRadioAPI as not installed or disabled | Install or enable the required dependency in Unity Mod Manager. |
| DoubleTrack says `waiting for the world layout` | Load into a railway world and wait for the scene to finish loading. Create a route afterward. |
| Normal layout is active while Hard is selected | Reload the world after saving the DoubleTrack mode. The saved selection does not replace rails already spawned in the current scene. |
| A junction was not switched automatically | Check whether DV Signals has an active reservation on the junction. Revised DV Route leaves protected junctions unchanged. |
| A Comms Radio route result says `ADS CHECK` | ADS is installed, enabled, and was checked. `No blocking ... conflict` means the proposed path is safe in ADS's current live reservation, occupancy, and signal snapshots; it does not mean the route is unavailable. When ADS blocks the original path, the radio shows `ADS WARNING` followed by `ADS ALTERNATE CONFIRMED` and the first differing track. |
| Flipping says no opposite-heading path exists | Flip excludes the active route's first segment and searches the live rail graph again. Some origins—especially dead ends or Hard-layout corridors—have no second departure path. The log records the excluded segment and search rejection counts. |
| AI Traffic does not yield to the route | Confirm DV Signals and AI Traffic are enabled, enable **Player Priority** in AI Traffic, and leave both DV Signals settings enabled in Revised DV Route. The F10 panel should report an active handoff. Existing AI trains inside an approach-locking or physically occupied block retain safety priority until clear. |
| The log says the route is waiting for a DV Signals block | Another train or signal currently owns an overlapping block. Keep the train stopped at the signal; Revised DV Route retries an initially unavailable block. If DV Signals invalidates a previously owned block, local reservation writes are suspended until a new route is selected so the two mods cannot enter an aspect-update loop. |
| The locomotive reverser alternates between forward and reverse | Stop automation and verify that the current 1.0.4 build is installed. Reverser changes now occur only for explicit `ReverseTrain` manoeuvres; rollback on a grade is stopped and recovered without changing direction. |
| AI or cruise control behaves unexpectedly | Stop the automation, take manual control, and report the situation with the mod log and the route/locomotive involved. |

## Gameplay performance

Revised DV Route performs route searches asynchronously and keeps recurring Unity main-thread work bounded. Release builds update RouteTracker five times per second, while the locomotive AI control loop runs approximately three times per second. Track speed calculations are cached, live switch repair uses a limited lookahead, and reflected ADS signal-authority matching is cached for 500 ms. Lost DV Signals reservations are not repeatedly reclaimed, avoiding repeated block rebuilds and network-wide aspect recalculation.

These safeguards are designed to keep normal routing and automation lightweight, including long routes. Actual frame-time behavior still depends on the active mod set, railway traffic, consist size, and hardware; a Unity or CPU profiler capture is the authoritative way to diagnose a machine-specific hitch.

## Building from source

Visual Studio 2022 (or MSBuild), the .NET Framework 4.8 Developer Pack, a Derail Valley build-99.7 installation, and CommsRadioAPI 1.0.3 are required.

```powershell
MSBuild RevisedDVRoute\RevisedDVRoute.csproj /t:Rebuild /p:Configuration=Release /p:DVInstallPath="C:\Steam\steamapps\common\Derail Valley"
```

The release archive is written to `RevisedDVRoute\bin\Release\RevisedDVRoute-1.0.4.zip`. The archive version is read from `Info.json`, and the WAV files are copied into `bin\Release\audio` before being packaged. Add `/p:BuildGameCopy=true` to copy the built mod files to the game's mod directory after a successful build.

For an isolated test build, select the **Test** solution configuration in Visual Studio/Rider or run:

```powershell
MSBuild RevisedDVRoute\RevisedDVRoute.csproj /t:Rebuild /p:Configuration=Test /p:DVInstallPath="C:\Steam\steamapps\common\Derail Valley"
```

Test outputs and its archive are written under `RevisedDVRoute\bin\Test\`, separately from Debug and Release. The Test configuration also copies the successfully built mod into `Derail Valley\Mods\RevisedDVRoute` automatically. Pass `/p:BuildGameCopy=false` only when you need an isolated Test build without deployment.

The build validates the game-managed assemblies and `Mods\CommsRadioAPI\CommsRadioAPI.dll` before compilation, so an incorrect game path produces an actionable failure.

## Credits

- **Wally (WallyCZ)** — original creator and author of DV Route Manager; this revised project is built upon his work.
- RouteSetter by zelmer69, a reference for CommsRadioAPI usage.
- Derail Valley by Altfuture.
