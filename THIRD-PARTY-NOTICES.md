# Third-Party Notices

ForgeSide is distributed under the [MIT License](./LICENSE).
This file lists the third-party components it uses.

## NuGet packages

| Package | Version | License |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 2.3.0 | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | 10.0.13 | MIT |
| [System.Management](https://github.com/dotnet/runtime) | 10.0.13 | MIT |
| [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) | 1.0.3967.48 | Microsoft Software License Terms |

The WebView2 Runtime is installed separately on the user's machine and is not
redistributed with ForgeSide.

## UI

`ForgeSide_src/forge_side_ui.html` is self-contained: there are no external
CDNs, third-party JavaScript libraries, or icon fonts. All icons are inline SVG.

## Project assets

- `ForgeSide_src/default_bg.jpg` and `ForgeSide_src/ico.ico` are project assets.
- Images under `screenshots/` are screenshots of ForgeSide and may include
  AI-generated images produced with Stable Diffusion models.

> Asset provenance and model license terms are confirmed by the project author
> before each public release.
