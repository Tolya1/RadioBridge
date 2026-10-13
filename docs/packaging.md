# Packaging and releases

RadioBridge ships as **one self-contained executable per platform**. The .NET runtime is inside it
and compressed, so users install nothing. The size is about 48 MB.

```bash
mise run package            # win-x64, linux-x64, osx-arm64 and osx-x64 into ./dist/<rid>/
mise run package:windows    # the Windows release zip, from a clean checkout
```

The package is a normal `dotnet publish` with `-p:SingleFile=true`, which the project file turns into
self-contained, single-file, compressed output. Nothing else is needed in the folder, and the release
zip holds just `RadioBridge.exe`. Specifically:

- **The documents are inside the executable.** The readme, the license and the third-party notices are
  embedded resources, shown by the window's **Readme**, **License** and **Third-party notices** buttons
  (and by a window that opens on the first start) and printed by `--readme` and `--licenses`. So no
  `README`, `LICENSE` or notices files need to ship next to it. The libraries inside the executable
  (MIT, BSD, Apache, OFL) require their copyright notices and license texts to accompany the binary,
  and the notices file carries the real texts, not just names. (This isn't legal advice: it is the
  usual way a single-file application meets those terms.)

- **SimConnect is never included.** It is Microsoft's, and its redistribution terms are unclear: the
  MSFS SDK EULA allows only "distributable code" and no document says this library is. RadioBridge
  uses the copy in the user's own MSFS install instead (see the README), which needs no SDK and ships
  nothing from Microsoft. A copy in `libs/` is for local builds only and is excluded from publishing.
- **Debug symbols are not included.** Skia alone ships 100 MB of them; a build step deletes them.
- **No trimming and no Native AOT.** They would shrink the file, but the settings and automation JSON
  use reflection and Avalonia relies on it. Revisit only with tests that cover the trimmed build.

## What the user sees

- On first start, Avalonia's native libraries (Skia, HarfBuzz and ANGLE, about 18 MB) are unpacked to
  `%TEMP%\.net\RadioBridge\<hash>\` (`$HOME/.net/RadioBridge/` on Linux and macOS). Later starts reuse
  them, which makes them quicker.
- The executable is **not signed**, so Windows SmartScreen shows a warning the first time: *More
  info › Run anyway*. Code signing needs a certificate; the release notes say so.
- Settings and logs live in the data folder described in the README, never next to the executable, so
  the file can sit anywhere, including a read-only folder.

## Platform notes

| Platform | Package | Notes |
|---|---|---|
| Windows x64 | `RadioBridge.exe` in a zip with the license and notices | Tested end to end (see below) |
| Linux x64 | `RadioBridge` | Run `chmod +x` after unzipping or copying. Needs the udev rule from `packaging/linux/` to open the panel |
| macOS arm64 / x64 | `RadioBridge` | Apple Silicon only runs signed code. A .NET single file can be signed ad hoc with `codesign --sign - RadioBridge`, and downloads need `xattr -dr com.apple.quarantine`. A real `.app` bundle is a folder, so it is out of scope for "one file" |

## Testing the package itself

The functional tests normally launch the build output. Point them at the packaged file instead and
the same scenarios run against exactly what users get:

```bash
mise run functional:package                     # publishes for this machine, then runs them
RADIOBRIDGE_APP=path/to/folder-or-exe mise run functional   # any package you already have
```

Run it against the unzipped release before publishing: it catches problems that only exist in the
single-file form, such as a missing native library.

## The third-party notices

`THIRD-PARTY-NOTICES.md` is generated: `mise run notices` assembles it from the verbatim license texts
in `packaging/notices/sources` (their origins are listed in that folder's README) and the package
versions the project restores. The generator fails when a dependency isn't covered by any component, and
`mise run notices:check` (part of `mise run ci`, and run by the release script) fails when the committed
file is out of date. So after updating a dependency: restore, `mise run notices`, and if it complains, add
the new component's license text to `sources` and the component to the generator.

## Making a release

1. Set `<Version>` in `Directory.Build.props`, commit and push.
2. `mise run ci`, then `mise run package:windows`. The script refuses to run with uncommitted
   changes, so the zip always matches a commit; it checks the notices are current, that the
   packaged exe really prints its license texts, and prints the SHA-256.
3. Unzip into an empty folder and run `RADIOBRIDGE_APP=<that folder> mise run functional`.
4. Create the release with the zip attached:

   ```bash
   gh release create v<version> dist/RadioBridge-<version>-win-x64.zip --target <commit> \
     --title "RadioBridge <version>" --notes-file <notes.md>
   ```

   The notes should state what is tested and what is not, the SHA-256, and the SmartScreen warning.
