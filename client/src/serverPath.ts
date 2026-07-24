import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

const SERVER_ASSEMBLY = "OIRF.LanguageServer";

function exeName(): string {
  return process.platform === "win32" ? `${SERVER_ASSEMBLY}.exe` : SERVER_ASSEMBLY;
}

/**
 * Resolves the language server executable.
 *
 * - Packaged install: the bundled, published server lives under `dist/server/` inside the
 *   extension (copied there by the repo's build pipeline, see README).
 * - Dev mode (F5 / `OIRF_LSP_DEV=1`): fall back to the sibling `server/` project's own
 *   `bin/Debug` output, so debugging the extension doesn't require a publish step first.
 */
export function resolveServerPath(context: vscode.ExtensionContext): string {
  const bundled = path.join(context.extensionPath, "dist", "server", exeName());
  if (fs.existsSync(bundled)) {
    return bundled;
  }

  const devCandidates = ["Debug", "Release"].map((config) =>
    path.join(
      context.extensionPath,
      "..",
      "server",
      "src",
      "OIRF.LanguageServer",
      "bin",
      config,
      "net9.0",
      exeName()
    )
  );

  for (const candidate of devCandidates) {
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }

  throw new Error(
    `OIRF Engine LSP: could not find the language server executable. Looked in:\n` +
      [bundled, ...devCandidates].join("\n") +
      `\nBuild the server first: dotnet build server/OIRF.LanguageServer.sln`
  );
}
