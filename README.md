# Articulated Buses

Source code for **Articulated Buses** mod for *Cities: Skylines II*.
The base game has no built-in support for articulated (bendy) buses. This mod adds it by spawning and driving a linked rear trailer section for compatible bus prefabs, and by bending the connection between the sections as the bus turns. It ships no assets of its own: players subscribe to articulated bus assets that depend on it.
The mod is intended as a temporary workaround and will be deprecated once the game gains native articulated-bus support. Removing it does not break save games.

## Published mod

- Paradox Mods: https://mods.paradoxplaza.com/mods/148570 (ModId 148570)
- Forum thread: https://forum.paradoxplaza.com/forum/threads/articulated-buses-mod.1930913/

The player description, the asset-creator how-to and the changelog ship with package. See [`TINB.ArticulatedBuses/LongDescription.md`](TINB.ArticulatedBuses/LongDescription.md) and [`TINB.ArticulatedBuses/changelog.md`](TINB.ArticulatedBuses/changelog.md).

## Repository layout

| Path | Contents |
|------|----------|
| `TINB.ArticulatedBuses/` | Actual Mod Source |
| `TINB.ArticulatedBuses.Tests/` | Unit tests for the geometry helpers |
| `TINB.ArticulatedBuses.sln` | Solution file |