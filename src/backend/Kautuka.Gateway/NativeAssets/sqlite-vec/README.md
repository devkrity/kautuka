These sqlite-vec v0.1.9 shared libraries are bundled with Kautuka and are tracked in this repository:

Expected published layout:

- sqlite-vec/linux-x64/vec0.so
- sqlite-vec/win-x64/vec0.dll

Runtime resolution order:

1. Kautuka:SqliteVec:ExtensionPath
2. App base directory + platform-specific packaged asset:
   - Linux x64: sqlite-vec/linux-x64/vec0.so
   - Windows x64: sqlite-vec/win-x64/vec0.dll

They come from [asg017/sqlite-vec v0.1.9](https://github.com/asg017/sqlite-vec/tree/v0.1.9), commit `e9f598abfa0c06b328d8fe5da9c3760cce74be10`, and are dual licensed under MIT or Apache-2.0. The release compliance inventory records the upstream source and SHA-256 for each binary. Full license texts are included with every release.
