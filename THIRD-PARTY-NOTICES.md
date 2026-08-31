# Third-party notices

Doll Remolding Lab is distributed under the MIT license (see `LICENSE`). It redistributes the
components listed below. Each remains under its own license and copyright.

Versions are the ones pinned in `src/Remold.App/Remold.App.csproj` and
`src/Remold.Core/Remold.Core.csproj` at the time of the release this file ships with.

**Not redistributed:** [3DMigoto](https://github.com/bo3b/3Dmigoto) is installed separately by the
user. No part of it ships in the release zip, and this app neither bundles nor modifies it. The same
goes for [Blender](https://www.blender.org/), which the app launches if it is already installed, and
for the game itself, whose files are read from the user's own install and never redistributed.

The release is a self-contained build, so it also carries the **.NET runtime** (Microsoft, MIT,
<https://github.com/dotnet/runtime>).

---

## UI

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| Avalonia (incl. `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Win32`, `Avalonia.X11`, `Avalonia.Native`, `Avalonia.FreeDesktop`, `Avalonia.Skia`, `Avalonia.Remote.Protocol`) | 11.3.20 | MIT | <https://github.com/AvaloniaUI/Avalonia> |
| Avalonia.Fonts.Inter | 11.3.20 | MIT (package); the embedded **Inter** typeface is SIL OFL 1.1 | <https://github.com/AvaloniaUI/Avalonia> · <https://github.com/rsms/inter> |
| Avalonia.Angle.Windows.Natives (Google ANGLE) | 2.1.25547.20250602 | BSD-3-Clause | <https://github.com/AvaloniaUI/angle> |
| SkiaSharp + `SkiaSharp.NativeAssets.Win32` | 2.88.9 | MIT | <https://github.com/mono/SkiaSharp> |
| HarfBuzzSharp + `HarfBuzzSharp.NativeAssets.Win32` | 8.3.1.1 | MIT | <https://github.com/mono/SkiaSharp> |
| MicroCom.Runtime | 0.11.0 | MIT | <https://github.com/kekekeks/MicroCom> |
| CommunityToolkit.Mvvm | 8.3.2 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| CommunityToolkit.HighPerformance | 8.4.0 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| Tmds.DBus.Protocol | 0.21.3 | MIT | <https://github.com/tmds/Tmds.DBus> |

`Avalonia.BuildServices` (11.3.2) is a build-time dependency only and is not redistributed.

## Assets and geometry

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| AssetsTools.NET | 3.0.2 | MIT | <https://github.com/nesrak1/AssetsTools.NET> |
| AssetsTools.NET.Addressables | 3.0.2 | MIT | <https://github.com/nesrak1/AddressablesTools> |
| AssetsTools.NET.Texture | 3.0.2 | MIT | <https://github.com/nesrak1/AssetsTools.NET> |
| AssetRipper.TextureDecoder | 1.3.0 | MIT | <https://github.com/AssetRipper/TextureDecoder> |
| SharpGLTF.Toolkit / .Core / .Runtime | 1.0.3 | MIT | <https://github.com/vpenades/SharpGLTF> |
| MathNet.Numerics | 5.0.0 | MIT | <https://github.com/mathnet/mathnet-numerics> |

## Imaging

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| SixLabors.ImageSharp | 3.1.11 | Six Labors Split License 1.0 (Apache-2.0 terms for qualifying use) | <https://github.com/SixLabors/ImageSharp> |
| BCnEncoder.Net | 2.3.0 | MIT OR Unlicense | <https://github.com/Nominom/BCnEncoder.NET> |
| DirectXTexNet | 1.0.7 | MIT | <https://github.com/deng0/DirectXTexNet> |

**Note on ImageSharp.** The dependency is pinned to the 3.x line on purpose. 3.x ships under the Six
Labors Split License 1.0, which grants Apache-2.0 terms to qualifying users, including open-source
projects and small businesses. ImageSharp 4.x and later require a commercial Six Labors license, so
this project does not track past 3.x. The full text is in the package's `LICENSE` file and at
<https://github.com/SixLabors/ImageSharp/blob/main/LICENSE>.

---

## License texts

### MIT License

Applies to every component marked MIT above, each under its own copyright holder (see the linked
upstream repository for the exact notice).

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### The Unlicense

BCnEncoder.Net is dual-licensed MIT **or** Unlicense, at the recipient's option. Full text:
<https://unlicense.org/>

### BSD-3-Clause (ANGLE)

Copyright 2018 The ANGLE Project Authors. All rights reserved.

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

### Apache License 2.0

The terms ImageSharp 3.x grants to qualifying users. Full text:
<https://www.apache.org/licenses/LICENSE-2.0>

### SIL Open Font License 1.1

The Inter typeface, embedded in `Avalonia.Fonts.Inter`. Copyright (c) 2016 The Inter Project
Authors. Full text: <https://openfontlicense.org/>
