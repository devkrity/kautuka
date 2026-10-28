# Development workflow

Use the root [developer guide](README.md) for setup, supported versions, run/debug, test, and formatting commands. The cross-platform setup entry point is `./setup_the_world.sh`; use `./setup_the_world.sh --check` for a read-only doctor report.

The committed VS Code configuration is **Launch Kautuka** for the full service stack and **Launch Gateway** for the Gateway alone. `npm run launch` is the equivalent full-stack command. Fixed ports are listed in [Runtime reference](../reference/runtime.md).

Run `npm run docs:check` for documentation links and Narad references. Run backend and frontend checks from the [developer guide](README.md#validation-commands).
