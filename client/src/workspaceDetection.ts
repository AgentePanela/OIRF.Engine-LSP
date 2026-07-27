import * as vscode from "vscode";

const ACTIVATION_THRESHOLD = 2;
const EXCLUDE_GLOB = "**/{bin,obj,node_modules,.git}/**";
const ENGINE_PROJECT_NAMES = ["Engine.Shared", "Engine.Client", "Engine.Server"];
const ENGINE_SOURCE_MARKERS = ["Engine.Shared.Prototypes", "[RegisterComponent(", "[DataField("];

export interface DetectionResult {
  score: number;
  activate: boolean;
  signals: string[];
}

/**
 * Weighted, best-effort check for "is this workspace an OIRF/Eptus engine project" - run once
 * before spawning the (comparatively expensive) dotnet language server process. Prototypes can
 * live under any resource root (not just a folder literally named "Resources" - the engine
 * supports registering additional, arbitrarily-named resource roots), so folder detection keys
 * off any ancestor directory named "Prototypes", never a fixed "Resources/Prototypes" prefix.
 */
export async function detectEngineWorkspace(): Promise<DetectionResult> {
  let score = 0;
  const signals: string[] = [];

  const prototypeFiles = await vscode.workspace.findFiles(
    "**/Prototypes/**/*.{yml,yaml}",
    EXCLUDE_GLOB,
    1
  );
  if (prototypeFiles.length > 0) {
    score += 2;
    signals.push("Prototypes/**/*.yml folder present");
  }

  const projectFiles = await vscode.workspace.findFiles(
    "**/*.{sln,slnx,csproj}",
    EXCLUDE_GLOB,
    50
  );
  for (const uri of projectFiles) {
    try {
      const bytes = await vscode.workspace.fs.readFile(uri);
      const text = Buffer.from(bytes).toString("utf8");
      if (ENGINE_PROJECT_NAMES.some((name) => text.includes(name))) {
        score += 2;
        signals.push(`${vscode.workspace.asRelativePath(uri)} references the engine`);
        break;
      }
    } catch {
      // unreadable project file - ignore, this is a best-effort heuristic
    }
  }

  const gitmodules = await vscode.workspace.findFiles("**/.gitmodules", EXCLUDE_GLOB, 1);
  if (gitmodules.length > 0) {
    try {
      const bytes = await vscode.workspace.fs.readFile(gitmodules[0]);
      const text = Buffer.from(bytes).toString("utf8");
      if (text.includes("OIRF.Engine")) {
        score += 1;
        signals.push(".gitmodules references OIRF.Engine");
      }
    } catch {
      // ignore
    }
  }

  const csharpFiles = await vscode.workspace.findFiles("**/*.cs", EXCLUDE_GLOB, 5);
  for (const uri of csharpFiles) {
    try {
      const bytes = await vscode.workspace.fs.readFile(uri);
      const text = Buffer.from(bytes).toString("utf8");
      if (ENGINE_SOURCE_MARKERS.some((marker) => text.includes(marker))) {
        score += 1;
        signals.push(`${vscode.workspace.asRelativePath(uri)} uses engine prototype/component APIs`);
        break;
      }
    } catch {
      // ignore
    }
  }

  return { score, activate: score >= ACTIVATION_THRESHOLD, signals };
}
