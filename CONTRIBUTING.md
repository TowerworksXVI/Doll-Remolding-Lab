# Contributing

## Build

Needs the .NET 10 SDK. Nothing else.

```
dotnet build Remold.slnx
```

## Test

```
dotnet test Remold.slnx
```

The Blender bridge has its own tests under `blender/`. The pure helpers run under plain Python
against a `bpy` stub, so Blender is not needed:

```
python -m unittest discover -s blender
```

The parts that read a real scene run inside Blender itself:

```
blender --background --factory-startup --python blender/bpy_test_remold_bridge.py
```

## Publish a release build

Two steps: a self-contained win-x64 publish, then a repack into the release layout — the exe alone
at the root, everything else under `app\`. The packed FOLDER is the release; it is zipped as-is.

```
dotnet publish src/Remold.App -p:PublishProfile=win-x64
dotnet run --project tools/PackRelease -- out/publish/win-x64 "out/release/Doll Remolding Lab"
```

Single-file is not an option: the GPU texture encoder's DirectXTex assembly is mixed-mode, and a
mixed-mode assembly cannot load out of a single-file bundle. The self-extract workaround trips AV
heuristics, and a folder suits an app that creates mod folders beside itself anyway.

Trimming stays off: Avalonia resolves controls and themes reflectively, and a trimmed build drops
types no static analysis can see.

## Code style

Follow whatever the file you are editing already does. There is no formatter to run and no style
config to satisfy; the tree is consistent enough to copy from.

Two habits the codebase keeps that are easy to miss:

- **Comments describe the code, not its history.** A comment earns its place by stating a
  constraint, an invariant, or a non-obvious why. It is not a changelog.
- **A disabled button says why.** Enablement and the reason shown on hover come from one function,
  so the two can never disagree.

## Pull requests

Welcome, on one condition. Write the PR yourself.

Call it hypocritical if you want. But I want you to tell me - in your own words, not a computer's - what you changed and why.
