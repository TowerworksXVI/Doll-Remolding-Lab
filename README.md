# Doll Remolding Lab

An authoring app for 3DMigoto mods for **Girls' Frontline 2: Exilium**. Pick a character or an
enemy, replace, hide or retexture their meshes, and build a standard, self-contained 3DMigoto mod
folder that anyone with 3DMigoto can drop in.

The app reads the game's own files to find every character, outfit and enemy, hands a mesh to
Blender for editing, and turns what comes back into the buffers, textures and `mod.ini` a 3DMigoto
mod is made of. Nothing about the game install is modified.

**New to the Lab? Start with the
[usage guide](https://docs.google.com/document/d/1ro1edRyNvT9rdRcQ_VapNkm8KevvTO9kpP-KeNpEa6Q/) —
a screenshot-led walk from install to a working mod.**

## How it works

The window has three steps, in order.

### 1. Pick

A searchable tree of every character and outfit found in the installed game, under a **Characters**
tab and an **Enemies** tab. Tick the outfits this mod covers, or tick a character to take all of
theirs at once. Double-click a row to open it in Edit.

### 2. Edit

The workbench for one outfit at a time.

- **Replace a mesh.** Open a part in Blender and the session carries that part and nothing else.
  Open it **with References** and the app sends the *whole outfit on one armature*, so a weight can
  be painted against any bone with the rest of the outfit visible around the edit. Only the part
  the session names is writable; everything else comes in as reference scenery. Click
  **Send to Lab** in Blender's sidebar panel and the edit lands back in the app.
- **Hide a mesh.** Delete a part's mesh in Blender and send, or hide it from the app. The built mod
  suppresses the vanilla draw.
- **Retexture.** Drop a PNG onto a texture slot, or open the slot in an image editor and save. Base
  colour, normal and RMO maps are picked up per submesh. Maps authored on a donor mesh in Blender's
  shader editor are collected the same way.

Every edit lives as a plain file in the mod project folder, revertable one file at a time.

### 3. Build

The change list is **derived**, never authored: the pane shows exactly what the Edit pane's state
produces, one row per Replace, Hide or Retexture, with warnings for changes that would ship nothing.
Untick a row to leave it out of this build.

Each row can be given a **toggle key**, and the whole mod can be given one of its own. Build writes
the mod folder plus a `.zip` for sharing.

### Install and Launch

**Install** copies the built folder into the `Mods\` folder beside the 3DMigoto loader set in
Settings. It reports folders already on the same hashes before overwriting anything, and never
deletes a mod it did not put there. **Launch** starts the loader, waits for it, then starts the
game. 3DMigoto has to hook the game process as it comes up, so the order matters.

## Requirements

- Windows 10 or 11, x64.
- **Girls' Frontline 2: Exilium**, installed. Steam and standalone installs are both detected; the
  folder can also be set by hand.
- A GFL2-configured 3DMigoto loader, installed separately. Recommended:
  [DollMI](https://github.com/TowerworksXVI/DollMI), this app's companion mod manager — its bundled
  `3dmigoto\` host ships GFL2-ready. Two third-party loaders are also validated: the
  [3Dmigoto Mod Loader](https://www.nexusmods.com/girlsfrontline2exilium/mods/4) on Nexus, and
  [SSMT4](https://github.com/StarBobis/SSMT4-Alpha) (create a GFL2 profile; SSMT fetches a 3DMigoto
  for it) — they work today, but support for third-party loaders may narrow to DollMI in a future
  release. The mods fire through a hook those loaders' `d3dx.ini` carries: plain stock 3DMigoto
  ships no GFL2 config and no hook, and a 3DMigoto set up for another game never attaches to GFL2,
  so neither works as-is. Point Settings at the loader itself — DollMI's or Nexus's
  `3DMigoto Loader.exe`, or SSMT's per-game `Run.exe` — and built mods install into the `Mods\`
  folder beside it. Until it is set, Install and Launch stay off and say why.
- **Blender**, for mesh editing only. Retexture-only and hide-only mods need no Blender. It is
  detected from `PATH`, the registry, and the usual install folders, and the exact executable can be
  set in Settings. Any Blender 4.x or 5.x works, validated on 4.3 and 5.1.
- An SSD is heavily recommended: outfits are prepared on disk when opened in Edit, and a working
  mod project grows to a few hundred MB.

## Install

Download the release zip and extract it anywhere — it unpacks to a single `Doll Remolding Lab`
folder; run `Doll Remolding Lab.exe` inside it. That folder is the whole app: a self-contained
win-x64 build, so there is no .NET runtime to install.

Settings, the mods library and the first-run record live **beside the exe**, so a copied folder
carries its state and extracting an update over the top keeps it. An update extracted over an
existing folder can leave files behind that the older version shipped and this one dropped; a fresh
folder is the clean route. Regenerable caches (the game index, thumbnails) live under
`%LOCALAPPDATA%\DollRemoldingLab` and are safe to delete.

## Blender integration

There is no addon to install. The bridge script ships beside the exe and is handed to Blender on the
command line each time a part is opened from the app, so Blender always runs the bridge that matches
the app.

Opening a part launches Blender with that part imported — on its own, or with the rest of the
outfit around it — the scene laid out (`Mod` for what ships, `Reference` for scenery that never
does), and a **Doll Remolding Lab** panel in the N-panel sidebar carrying **Check mesh** and
**Send to Lab**. Check runs a sanity pass that blocks a Send on problems which would break the
deliverable, and warns on likely mistakes. glTF export settings are never the modder's problem:
Send always exports with the settings the pipeline needs.

## Toggle keys

A toggle key is bound in the Build pane and written into the mod's `mod.ini` as a 3DMigoto `key =`
binding. Modifiers are allowed (`F6`, `CTRL SHIFT H`).

- Toggles **start ON** by default. A freshly installed mod is fully visible, and a press holds for that
  run only.
- Each keyed change sets how it starts and what its off state leaves on screen. A new mesh switched off
  either reverts to the character's own part or leaves nothing there.
- Two changes bound to the same key toggle together. The change list marks the shared key.
- **F10** is 3DMigoto's own key: it reloads all installed mods without restarting the game.

## Known limits

- **One mod per character at a time.** Two mods that override the same meshes fight over the draw,
  and this app does not decide the winner. Install reports the overlap by folder name, between mods
  this app built, so the choice is made deliberately.
- **Mods are built against the current game version.** A game update that changes a character's
  meshes can leave a built mod pointing at geometry that no longer matches. Rebuild after an update.
- **Not every part can be mesh-replaced.** Expression-driven meshes (faces) and meshes with reduced
  skins refuse a Replace, and the Edit pane says why on the part. Hide and Retexture still work on
  them.
- Texture slots other than base colour, normal and RMO are not emitted yet. An edit bound to one is
  reported at build time rather than silently dropped.

## Disclaimer

Modding a game may violate that game's terms of service. Use this tool at your own risk. The
consequences of using a mod, up to and including action taken against a game account, are the
user's own.

This is an unofficial, non-commercial fan project. It is not affiliated with, endorsed by, or
connected to the publisher or developers of Girls' Frontline 2: Exilium, nor to the 3DMigoto
project. All game assets, characters and trademarks belong to their respective owners. The app
ships no game content: it reads the copy of the game already installed on the machine it runs on.

## License

MIT. See [LICENSE](LICENSE).

Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Credits

- [3DMigoto](https://github.com/bo3b/3Dmigoto) — the mod loader every mod this app builds runs on.
  Installed separately by the user, not redistributed here.
- [GIMI](https://github.com/SilentNightSound/GI-Model-Importer) — the 3DMigoto build this app
  targets, installed as its
  [GFL2 configuration](https://www.nexusmods.com/girlsfrontline2exilium/mods/4) on Nexus. Installed
  separately by the user, not redistributed here.
- [SSMT4](https://github.com/StarBobis/SSMT4-Alpha) — an alternative loader the built mods also
  run on.
- [Avalonia](https://avaloniaui.net/) — the UI framework the app is built on.
- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) — reading Unity asset bundles.
- [SharpGLTF](https://github.com/vpenades/SharpGLTF) — glTF/glb import and export.
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) — image decode and encode.
- [DirectXTexNet](https://github.com/deng0/DirectXTexNet) — DirectXTex's BC7 compressor, run on the
  GPU.
- [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) — BC texture compression on the CPU.
- [Math.NET Numerics](https://numerics.mathdotnet.com/) — the linear algebra behind bone-palette recovery.
- [Blender](https://www.blender.org/) — the mesh editor the bridge drives.
