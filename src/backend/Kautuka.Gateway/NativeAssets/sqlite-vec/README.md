These sqlite-vec v0.1.9 shared libraries are bundled with Kautuka and are tracked in this repository:

Expected published layout:

- sqlite-vec/linux-x64/vec0.so
- sqlite-vec/linux-arm64/vec0.so
- sqlite-vec/osx-x64/vec0.dylib
- sqlite-vec/osx-arm64/vec0.dylib
- sqlite-vec/win-x64/vec0.dll

Runtime resolution order:

1. Kautuka:SqliteVec:ExtensionPath
2. App base directory + platform-specific packaged asset:
   - Linux x64 and arm64: sqlite-vec/linux-<architecture>/vec0.so
   - macOS Intel and Apple silicon: sqlite-vec/osx-<architecture>/vec0.dylib
   - Windows x64: sqlite-vec/win-x64/vec0.dll

They come from [asg017/sqlite-vec v0.1.9](https://github.com/asg017/sqlite-vec/tree/v0.1.9), commit `e9f598abfa0c06b328d8fe5da9c3760cce74be10`, and are dual licensed under MIT or Apache-2.0. The release compliance inventory records the upstream source and SHA-256 for each binary. Full license texts are included with every release.
