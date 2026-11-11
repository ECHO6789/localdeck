# Third-party notices

LocalDeck uses open-source NuGet and npm packages. Their licenses remain in
their respective package distributions.

- Microsoft.Web.WebView2
- Microsoft.Data.Sqlite / SQLitePCLRaw
- System.Management
- HidSharp
- NAudio
- YamlDotNet
- Microsoft.Diagnostics.Tracing.TraceEvent (MIT)
- React / React DOM
- Vite
- Hysteria2 v2.13.0 (MIT), distributed as the client engine under
  `vendor/hysteria`; source and artifact hash are recorded in
  `vendor/hysteria/SOURCE.txt`

LocalDeck does not redistribute Twinkle Tray, Clash Verge, Logitech G HUB,
Snipaste, Everything, Machenike Control Center, EarTrumpet, HWiNFO, or WizTree.

PGFNEX is accessed only when the vendor driver is already installed by the
computer manufacturer. No vendor binary is included in this repository.

## Windows GPU Unlock

- Project: https://github.com/jatinop7/gpu-unlock-windows
- License: MIT
- Copyright (c) 2026 Windows GPU Unlock contributors

LocalDeck reimplements the Windows inventory and compatibility-report portion
inside its own service and embeds the public profile state for the project's
reference system. No EFI application, pre-built boot binary, or hardware write
payload is included in this repository.

## DLSS Unlocked

- Project: https://github.com/ShyVortex/dlss-unlocked
- License file: MIT, Copyright (c) 2024 Artur Graniszewski

LocalDeck does not redistribute the standalone release by default. On an
explicit user action it downloads the pinned upstream package, verifies the
GitHub-published SHA-256 digest, and deploys it into the selected game folder.
That package contains additional third-party files governed by the licenses
inside its `Licenses/` directory, including NVIDIA Streamline components.

### MIT license text for the integrations above

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
