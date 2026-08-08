# Doll Remolding Lab

An authoring app for 3DMigoto mods for **Girls' Frontline 2: Exilium** that fully supports model edits and/or replacement. Pick a character, an enemy or a weapon, then replace, hide or retexture its meshes and build a standard, self-contained 3DMigoto mod folder that anyone with 3DMigoto can drop in.

The app reads the installed game's files to find every character, outfit, enemy and weapon, hands meshes to Blender for editing, and packs the results into the buffers, textures and `mod.ini` that make up a 3DMigoto mod. The game install itself is never modified.

**New to the Lab? Start with the [usage guide](https://docs.google.com/document/d/1ro1edRyNvT9rdRcQ_VapNkm8KevvTO9kpP-KeNpEa6Q/), a screenshot-led walkthrough from install to a working mod.** The [FAQ](FAQ.md) covers everything else.

## How it works

The window has three steps, in order.

### 1. Pick

A searchable tree of everything found in the installed game, under **Characters**, **Enemies** and **Weapons** tabs. Weapons and outfits are grouped by their associated character.

Check the outfits you want to edit, or check a character to take all of theirs at once. Double-click a row to open it in Edit.

### 2. Edit

The workbench, for one outfit or weapon at a time.

- **Replace a mesh.** Open a part in Blender to edit just that part. The armature carries every usable bone, so weighting isn't limited to the bones the part started with. Open it **with References** to bring in the rest of the outfit around it on the same armature. Only the opened part is writable; everything else comes in as reference scenery. Click **Send to Lab** in Blender's sidebar and the edit lands back in the app.
- **Hide a mesh.** Delete a part's mesh in Blender and send it, or just hide it from the app. The built mod stops the game from drawing that part.
- **Retexture.** Drop a PNG onto a texture slot, or open the slot in an image editor and save. Base colour, normal and RMO maps are handled per submesh. Textures authored on a donor mesh in Blender's shader editor are picked up too.

Every edit is a plain file in the mod project folder and can be reverted one file at a time.

### 3. Build

The change list is generated from the Edit pane: one row per Replace, Hide or Retexture. Uncheck a row to leave it out of this build.

Each row can be given a **toggle key**, and the whole mod can have one of its own. Build writes the mod folder plus a `.zip` for sharing.

Press the "Build" button, and the Lab will assemble a working 3DMigoto mod for you.

### Install and Launch

**Install** copies the built mod into the `Mods\` folder beside the 3DMigoto loader set in Settings. It warns about already-installed copies before overwriting anything, and it never deletes a mod it didn't install. **Launch** starts the loader, waits for it, then starts the game. 3DMigoto has to hook the game as it starts, so the order matters.

## Requirements

- Windows 10 or 11, x64.
- **Girls' Frontline 2: Exilium**, with game files downloaded (launched to main menu at least once).
- A GFL2-configured 3DMigoto loader, installed separately. Recommended: [DollMI](https://github.com/TowerworksXVI/DollMI), this app's companion mod manager. Two third-party loaders are also validated: the [3Dmigoto Mod Loader](https://www.nexusmods.com/girlsfrontline2exilium/mods/4) on Nexus, and [SSMT4](https://github.com/StarBobis/SSMT4-Alpha).
- **Blender**, for mesh editing only. Any Blender 4.x or 5.x works, validated on 4.3 and 5.1.
- An SSD is heavily recommended for performance. Large mod projects may exceed 100MB each.

## Install

Download the release zip and extract it anywhere. Run `Doll Remolding Lab.exe`.

Set paths for GF2_Exilium.exe and Blender if they weren't auto-detected. Optional: set the 3DMigoto path to DollMI's or Nexus's `3DMigoto Loader.exe`, or SSMT's per-game `Run.exe`.

On first run and after an update, there will be a short crawl on startup to gather character data.

## Update

Extract over the existing folder.

## Blender integration

There is no separate addon to install. It will appear within the Blender windows opened by the app.

Opening a part launches Blender with the part already imported, on its own or with the rest of the outfit around it. The scene comes organized (`Mod` for what ships, `Reference` for scenery that never does), with all supported bones, and a **Doll Remolding Lab** panel in the sidebar carrying **Check mesh** and **Send to Lab**. Check mesh blocks a Send on problems that would break the mod and warns on likely mistakes. Send to Lab always exports with the right glTF settings, so the export dialog is never your problem.

## Toggle keys

A toggle key is set in the Build pane and written into the mod's `mod.ini` as a standard 3DMigoto `key =` binding. Modifiers are allowed (`F6`, `CTRL SHIFT H`).

- Toggles start ON. A freshly installed mod is fully visible, and a key press lasts until the game closes.
- Each keyed change picks its starting state and what its off state shows: the character's original part, or nothing.
- Two changes on the same key toggle together. The change list marks the shared key.
- **F10** is 3DMigoto's own reload key: it reloads all installed mods without restarting the game.

## Known limits

- **One mod per outfit at a time.** Two mods that change the same meshes fight over the draw, and the app doesn't pick a winner. Install reports overlaps between mods it built, by folder name, so the choice is yours.
- **Mods are built against the current game version.** A game update that changes a character's meshes can break a built mod. Rebuild after an update.
- **Not every part can be mesh-replaced.** Faces refuse a Replace (they are driven by the game's expression system), and so do meshes that swing on the game's spring bones (charms and some weapon parts). The Edit pane says why on the part. Hide and Retexture still work on them.
- Texture slots other than base colour, normal and RMO are not emitted yet. An edit on one is reported at build time rather than silently dropped.

## The technique

This game skins its characters before the GPU ever sees them, so ordinary 3DMigoto mesh replacement doesn't work: every draw arrives already posed, with no bones bound. The Lab recovers the live bone matrices from the posed vertices each frame and re-skins new geometry with them. [PALETTE_RECOVERY_GUIDE.md](PALETTE_RECOVERY_GUIDE.md) explains the whole technique in game-agnostic terms, including what it takes to implement it on another game with the same wall.

## Disclaimer

Modding a game may violate its terms of service. Use this tool at your own risk. Any consequences of using a mod, up to and including action against a game account, are yours.

This is an unofficial, non-commercial fan project. It is not affiliated with, endorsed by, or connected to the publisher or developers of Girls' Frontline 2: Exilium, nor to the 3DMigoto project. All game assets, characters and trademarks belong to their respective owners. The app ships no game content. The mods provide no gameplay advantage.

## License

MIT. See [LICENSE](LICENSE).

Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

- [3DMigoto](https://github.com/bo3b/3Dmigoto) — the mod loader every mod this app builds runs on. Installed separately by the user, not redistributed here.
- [GIMI](https://github.com/SilentNightSound/GI-Model-Importer) — the 3DMigoto build this app targets, installed as its [GFL2 configuration](https://www.nexusmods.com/girlsfrontline2exilium/mods/4) on Nexus. Installed separately by the user, not redistributed here.
- [SSMT4](https://github.com/StarBobis/SSMT4-Alpha) — an alternative loader the built mods also run on.
- [Avalonia](https://avaloniaui.net/) — the UI framework the app is built on.
- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) — reading Unity asset bundles.
- [SharpGLTF](https://github.com/vpenades/SharpGLTF) — glTF/glb import and export.
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — image decode and encode.
- [DirectXTexNet](https://github.com/deng0/DirectXTexNet) — DirectXTex's BC7 compressor, run on the GPU.
- [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) — BC texture compression on the CPU.
- [Math.NET Numerics](https://numerics.mathdotnet.com/) — the linear algebra behind bone-palette recovery.
- [Blender](https://www.blender.org/) — the mesh editor the bridge drives.
