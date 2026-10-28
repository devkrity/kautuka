import { spawnSync } from "node:child_process";
import process from "node:process";
import readline from "node:readline/promises";

const stagedPaths = getStagedPaths();
const hasBackendChanges = stagedPaths.some((filePath) =>
  filePath.startsWith("src/backend/"),
);
const hasDocsChanges = stagedPaths.some((filePath) =>
  filePath.startsWith("docs/"),
);

if (!process.stdin.isTTY || !process.stdout.isTTY || process.env.CI) {
  if (hasBackendChanges && !hasDocsChanges) {
    console.error(
      "Non-interactive commit blocked: staged changes under src/backend require staged documentation updates under docs/.",
    );
    process.exit(1);
  }

  console.log(
    "Skipping docs confirmation prompt because the commit is running in a non-interactive environment.",
  );
  process.exit(0);
}

const rl = readline.createInterface({
  input: process.stdin,
  output: process.stdout,
});

try {
  while (true) {
    const answer = await rl.question(
      "Have you updated the relevant docs for this change? [yes/no/skip] ",
    );
    const normalized = normalizeAnswer(answer);
    if (normalized) {
      exitForAnswer(normalized);
    }

    console.log("Please answer yes, no, or skip.");
  }
} finally {
  rl.close();
}

function normalizeAnswer(value) {
  if (!value) {
    return null;
  }

  switch (value.trim().toLowerCase()) {
    case "y":
    case "yes":
      return "yes";
    case "n":
    case "no":
      return "no";
    case "s":
    case "skip":
      return "skip";
    default:
      return null;
  }
}

function getStagedPaths() {
  const result = spawnSync("git", ["diff", "--cached", "--name-only"], {
    stdio: ["ignore", "pipe", "inherit"],
    encoding: "utf8",
  });

  if (result.error) {
    console.error(
      `Failed to inspect staged files for docs confirmation: ${result.error.message}`,
    );
    process.exit(1);
  }

  if ((result.status ?? 1) !== 0) {
    console.error("Failed to inspect staged files for docs confirmation.");
    process.exit(result.status ?? 1);
  }

  return result.stdout
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function exitForAnswer(answer) {
  switch (answer) {
    case "yes":
      console.log("Docs confirmation: yes.");
      process.exit(0);
      break;
    case "skip":
      console.log("Docs confirmation: skipped.");
      process.exit(0);
      break;
    case "no":
      console.error(
        "Docs confirmation: no. Update the relevant docs or rerun with skip if you are intentionally deferring documentation.",
      );
      process.exit(1);
      break;
    default:
      console.error("Invalid docs confirmation state.");
      process.exit(1);
      break;
  }
}