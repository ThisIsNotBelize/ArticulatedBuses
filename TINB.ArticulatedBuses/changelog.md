# v1.0.3
- Fix: resolved a random crash to desktop that could happen during normal play (most often while buses were being reassigned between lines, or returning to the depot).
- Fix: when a bus is moved to a different line, its trailer now correctly takes on the new line's colour instead of keeping the old one.

# v1.0.2
- Fix: upgrading or extending a bus depot no longer crashes to desktop when articulated buses are stationed there (a crash that could occur in 1.0.1).
- Fix: articulated buses now garage inside the depot instead of surface-parking, so the trailer no longer overhangs and blocks the depot driveway.

# v1.0.1
- Fix: parked articulated buses are now un-spawning, so they no longer block the depot driveway or hold up other buses. Caveat: no articulated bus can be observed parked, but that's the trade-off.

# v1.0.0
Initial release.

- Articulated (bendy) bus support: spawns and drives a linked rear trailer for compatible bus assets.
- Smooth deforming bellows via a rigid Vehicle Connection bone chain (one bone, or several for a finer accordion).
- Trailer livery and transport-line colour kept in sync with the front; custom repaints follow.
- Trailer boarding/alighting doors are used in game.
- Asset Icon Creator support: the trailer is included in the captured icon.
- No custom save data: removing the mod is safe — the trailer disappears and the front keeps working.
