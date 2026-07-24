import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";
import { detectEngineWorkspace } from "./workspaceDetection";
import { resolveServerPath } from "./serverPath";

let client: LanguageClient | undefined;
let outputChannel: vscode.OutputChannel;
let statusBarItem: vscode.StatusBarItem;

interface StatusParams {
  state: "indexing" | "ready" | "notEngineWorkspace";
  prototypeCount: number;
  componentCount: number;
}

function renderStatus(status: StatusParams): void {
  switch (status.state) {
    case "indexing":
      statusBarItem.text = "$(sync~spin) OIRF Engine";
      statusBarItem.tooltip = "OIRF Engine LSP: indexing the workspace (prototypes/components)...";
      break;
    case "ready":
      statusBarItem.text = `$(check) OIRF Engine`;
      statusBarItem.tooltip =
        `OIRF Engine LSP: ready — ${status.prototypeCount} prototype type(s), ` +
        `${status.componentCount} component type(s).`;
      break;
    case "notEngineWorkspace":
      statusBarItem.text = "$(circle-slash) OIRF Engine";
      statusBarItem.tooltip = "OIRF Engine LSP: this workspace doesn't look like an OIRF/Eptus engine project.";
      break;
  }
  statusBarItem.show();
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  outputChannel = vscode.window.createOutputChannel("OIRF Engine LSP");
  context.subscriptions.push(outputChannel);

  statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
  statusBarItem.command = "oirf-engine-lsp.restartServer";
  context.subscriptions.push(statusBarItem);

  context.subscriptions.push(
    vscode.commands.registerCommand("oirf-engine-lsp.forceActivate", async () => {
      outputChannel.appendLine("Force-activating (bypassing workspace detection)...");
      await startServer(context);
    }),
    vscode.commands.registerCommand("oirf-engine-lsp.restartServer", async () => {
      outputChannel.appendLine("Restarting server...");
      await stopServer();
      await startServer(context);
    })
  );

  const detection = await detectEngineWorkspace();
  outputChannel.appendLine(
    `Workspace detection score=${detection.score} (threshold=2): ${detection.signals.join(", ") || "no signals found"}`
  );

  if (!detection.activate) {
    outputChannel.appendLine(
      "Not an OIRF/Eptus engine workspace - server not started. " +
        "Run 'OIRF Engine LSP: Force Activate' to override."
    );
    return;
  }

  await startServer(context);
}

async function startServer(context: vscode.ExtensionContext): Promise<void> {
  if (client) {
    outputChannel.appendLine("Server already running.");
    return;
  }

  let command: string;
  try {
    command = resolveServerPath(context);
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    outputChannel.appendLine(message);
    void vscode.window.showErrorMessage(
      "OIRF Engine LSP: could not locate the language server executable. See the 'OIRF Engine LSP' output channel."
    );
    return;
  }

  outputChannel.appendLine(`Starting server: ${command}`);

  const serverOptions: ServerOptions = {
    run: { command, transport: TransportKind.stdio },
    debug: { command, transport: TransportKind.stdio },
  };

  const clientOptions: LanguageClientOptions = {
    // Prototype YAML files can live under any ancestor folder literally named "Prototypes",
    // in any resource root - not just a fixed "Resources/Prototypes" path.
    documentSelector: [
      { scheme: "file", language: "yaml", pattern: "**/Prototypes/**/*.yml" },
      { scheme: "file", language: "yaml", pattern: "**/Prototypes/**/*.yaml" },
    ],
    outputChannel,
    synchronize: {
      fileEvents: [
        vscode.workspace.createFileSystemWatcher("**/*.cs"),
        vscode.workspace.createFileSystemWatcher("**/Prototypes/**/*.{yml,yaml}"),
        vscode.workspace.createFileSystemWatcher("**/Textures/**"),
        vscode.workspace.createFileSystemWatcher("**/Shaders/**"),
      ],
    },
  };

  client = new LanguageClient(
    "oirfEngineLsp",
    "OIRF Engine Language Server",
    serverOptions,
    clientOptions
  );

  renderStatus({ state: "indexing", prototypeCount: 0, componentCount: 0 });
  client.onNotification("oirf/status", (status: StatusParams) => renderStatus(status));

  await client.start();
  outputChannel.appendLine("Server started.");
}

async function stopServer(): Promise<void> {
  if (!client) {
    return;
  }
  const toStop = client;
  client = undefined;
  statusBarItem.hide();
  await toStop.stop();
}

export async function deactivate(): Promise<void> {
  await stopServer();
}
