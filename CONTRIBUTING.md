# Contributing to ForgeSide

Thanks for taking a look! Bug reports, ideas, and pull requests are all welcome.

## Before you start

- For a large change, open an issue first so we can agree on the direction.
- Keep the scope of a pull request focused; one topic per PR is much easier to review.

## Building

ForgeSide targets .NET 10 on Windows.

```powershell
git clone https://github.com/TohnoSeika/ForgeSide.git
cd ForgeSide

dotnet build ForgeSide_src/ForgeSide.csproj

dotnet publish ForgeSide_src/ForgeSide.csproj `
  -c Release -r win-x64 --self-contained false `
  -o publish
```

The app UI lives in `ForgeSide_src/forge_side_ui.html` and is loaded by the
WinForms host through WebView2. UI translations are defined in the same file,
one dictionary per language.

## Code style

- Keep methods short and names descriptive.
- Avoid deep nesting; early returns are preferred.
- Comments explain *why*, not *what*.
- Match the style of the surrounding code.

## Submitting a pull request

1. Fork the repository and create a branch from `master`.
2. Make your change and build it locally.
3. Describe what changed and why in the PR description, including screenshots
   for UI changes.
4. Make sure no build artifacts, personal paths, or credentials are included.

## Reporting security issues

Please do not open a public issue for security problems. See [SECURITY.md](./SECURITY.md).
