## About this mod
The base game currently has no built-in support for **articulated buses**. This mod provides a **temporary workaround**.
Players do not need to configure anything. Subscribe to an articulated bus asset that is requires this mod, that's it.

## How-to for creators
This mod is designed to be as update-safe as possible if native articulated-bus support is added later.

- No custom data is stored in prefabs or save games.
- Assets and saves remain usable without the mod.
- The mod works mainly on a cosmetic/rendering level.
- It applies its logic to any **bus-type car prefab** with a **Car Tractor** component.

### What this mod does
- Extends the game's existing car/truck trailer logic to buses.
- Enables trailers for public-transport bus prefabs with a **Car Tractor** component.
- Uses parts of the train/tram connection logic for walkways and bellows.
- Supports multiple **Vehicle Connection** bones for smoother accordion-style bending.

### 1. Preface
Use two separate models and prefabs **Bus front** and **Trailer**
Import both as vanilla-compatible `.fbx` files with axis orientation: **-Z Forward, Y Up**.

### 2. Bone setup and rigging
Rig the bending vertices to one or more **Vehicle Connection** bones.

For a simple joint:
- Use one **Vehicle Connection** bone.
- This is fully sufficient and works similarly to trains and trams.
- Recommended for LOD1 and LOD2.

For smoother accordion bending:
- Use a parented chain of connection bones.
- Run the chain from the body toward the gap.
- Set every connection bone to **Vehicle Connection** in the editor.

Chain example: root bone → connection bone → connection bone → … → final connection bone

Additional setup notes:
- The final connection bone sits at the gap between both sections. It acts as the actual connection point.
- The mod distributes the bend across the chain. More bones create smoother bending.
- All bones must be oriented -Z Forward (as the prefab models).

### 3. Front section: main bus
Import the front section like a normal bus as **Car Prefab**.
Add a **Car Tractor** component and set:
- **Fixed Trailer**: your trailer asset
- **Trailer Type**: **Fixed**

### 4. Rear section: trailer
Import the rear section as a **Car Trailer Prefab** and set:
- **Trailer Type**: **Fixed**
- **Fixed Tractor**: leave empty

**Do not** link the trailer back to the front manually (a two-way prefab link crashes the game when a save is loaded).
**Do not** try to reuse trailer prefabs for multiple bus fronts. Trailers should be unique trailer per front section (won't break anything, but leads to only one bus with a working trailer due to game limitations. Incorrect setups are logged with both front prefab names `…/Cities Skylines II/Logs/TINB.ArticulatedBuses.session.log`.

### 5. Colour properties
To make the trailer follow the front section's livery and transport-line colour add a **Color Properties** component to the trailer. Configure it like the bus front. Set it to "Brand".
The mod then matches trailer colour, per-vehicle shade and repaint changes made to the front section automatically.

### Bonus: Asset Icon Creator support
When using [Asset Icon Creator by TDW](https://mods.paradoxplaza.com/mods/105288/Windows), the mod automatically spawns trailers for snapshots. This allows icons to show the full articulated bus (in rare cases this may not work - then just reload the asset in the editor and try again).

## Disclaimer
* This mod is a **temporary solution**. Once the game adds native articulated-bus support, this mod will become obsolete and **deprecated**.
* **Removing this mod will not break save games.** The mod stores no custom save data and only uses vanilla game data.
* Not required, but recommended before removal:
	> 1. Load your save game.
	> 2. Open the mod options.
	> 3. Run the pre-removal clean-up.
	> 4. Save the clean save game.
	> 5. Remove the mod.

Final note: this mod was created with some help from coding agents.
