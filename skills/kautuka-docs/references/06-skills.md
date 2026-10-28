# Skills

A skill is a package containing `SKILL.md` and optional references, assets, or scripts. Manage packages on `/skills`; attach them per agent in **Settings → Agents → Skills**.

## Import and use

1. Validate the archive before import.
2. Review its name, instructions, resources, scripts, requested environment keys, and source metadata.
3. Import and enable it.
4. Attach it to an agent as:
   - **Disabled**: unavailable.
   - **Trusted**: the agent may load and use it normally.
   - **Ask first**: skill use is approval-gated; scripts require an enabled sandbox.

Skills are advertised to the model by name and must be loaded through the `load_skill` tool. A skill name is not itself a callable function.

The shipped `kautuka-docs` package is a system-owned help skill. It is installed on startup, enabled on first install, preserves an administrator’s enabled/disabled choice on refresh, and cannot be deleted. Shipped default skills and MCP packages remain independent of agent configuration.

## Endpoints and verification

- List/detail: `GET /api/skills`, `GET /api/skills/{skillId}`
- Validate/import: `POST /api/skills/validate`, `POST /api/skills/import`
- Update/delete: `POST /api/skills/{skillId}`, `DELETE /api/skills/{skillId}`
- User credential keys used by skills: `/api/credentials/user`

Verify the package appears as ready, is enabled, and is attached to the intended agent with the expected mode.
