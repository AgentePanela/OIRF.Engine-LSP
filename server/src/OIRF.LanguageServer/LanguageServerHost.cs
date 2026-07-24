using Microsoft.Extensions.Logging;
using OIRF.LanguageServer.Features;
using OIRF.LanguageServer.Logging;
using OIRF.LanguageServer.Workspace;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Server;

namespace OIRF.LanguageServer;

public static class LanguageServerHost
{
    private static readonly TextDocumentSelector PrototypeSelector = new(
        new TextDocumentFilter { Pattern = "**/Prototypes/**/*.yml" },
        new TextDocumentFilter { Pattern = "**/Prototypes/**/*.yaml" });

    public static async Task RunAsync(string[] args)
    {
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder
            .AddProvider(new StderrLoggerProvider())
            .SetMinimumLevel(LogLevel.Information));

        var documentStore = new OpenDocumentStore();
        EngineWorkspaceManager? manager = null;
        ITextDocumentLanguageServer? textDocument = null;
        ILanguageServer? languageServer = null;

        // Client-facing status (see client/src/extension.ts's status bar item) - not part of the
        // LSP spec, just a plain custom notification. "indexing" fires both for the initial
        // workspace load and for every debounced rescan triggered by a .cs save.
        void PublishIndexing() =>
            languageServer?.SendNotification("oirf/status", new StatusParams("indexing", 0, 0));

        void PublishReadyStatus()
        {
            if (manager is null)
                return;

            var state = manager.IsEngineWorkspace ? "ready" : "notEngineWorkspace";
            languageServer?.SendNotification(
                "oirf/status",
                new StatusParams(state, manager.Schema.PrototypesByTypeKey.Count, manager.Schema.ComponentsByName.Count));
        }

        void ValidateAndPublish(DocumentUri uri, string text)
        {
            if (manager is null || !manager.IsEngineWorkspace || textDocument is null)
                return;

            var diagnostics = PrototypeValidator.Validate(text, manager.Schema).ToList();
            textDocument.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = uri,
                Diagnostics = diagnostics,
            });
        }

        void RevalidateAllOpenDocuments()
        {
            foreach (var (uri, text) in documentStore.Snapshot())
                ValidateAndPublish(uri, text);
        }

        // Pinned to the Func<..., Task> delegate type explicitly: the OmniSharp builder has both
        // Action<...> and Func<..., Task> overloads for the same registration method, and an
        // inline lambda's natural type is ambiguous between them.
        Func<DidOpenTextDocumentParams, CancellationToken, Task> onDidOpen = (request, _) =>
        {
            var uri = request.TextDocument.Uri;
            var text = request.TextDocument.Text;
            documentStore.Set(uri, text);
            ValidateAndPublish(uri, text);
            return Task.CompletedTask;
        };

        Func<DidChangeTextDocumentParams, CancellationToken, Task> onDidChange = (request, _) =>
        {
            var uri = request.TextDocument.Uri;
            var text = request.ContentChanges.LastOrDefault()?.Text;
            if (text is not null)
            {
                documentStore.Set(uri, text);
                ValidateAndPublish(uri, text);
            }

            return Task.CompletedTask;
        };

        Func<DidSaveTextDocumentParams, CancellationToken, Task> onDidSave = (request, _) =>
        {
            if (documentStore.TryGet(request.TextDocument.Uri, out var text))
                ValidateAndPublish(request.TextDocument.Uri, text);

            return Task.CompletedTask;
        };

        Func<DidCloseTextDocumentParams, CancellationToken, Task> onDidClose = (request, _) =>
        {
            documentStore.Remove(request.TextDocument.Uri);
            return Task.CompletedTask;
        };

        Func<DidChangeWatchedFilesParams, CancellationToken, Task> onDidChangeWatchedFiles = (request, _) =>
        {
            foreach (var change in request.Changes)
            {
                var path = change.Uri.GetFileSystemPath();
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    manager?.NotifyCSharpFileChanged();
                else
                    manager?.NotifyResourceFileChanged();
            }

            return Task.CompletedTask;
        };

        Func<CompletionParams, CancellationToken, Task<CompletionList>> onCompletion = (request, _) =>
        {
            if (manager is { IsEngineWorkspace: true } && documentStore.TryGet(request.TextDocument.Uri, out var text))
            {
                var items = CompletionHandler.Handle(text, request.Position, manager.Schema, manager.Resources).ToList();
                return Task.FromResult(new CompletionList(items));
            }

            return Task.FromResult(new CompletionList());
        };

        Func<HoverParams, CancellationToken, Task<Hover?>> onHover = (request, _) =>
        {
            if (manager is { IsEngineWorkspace: true } && documentStore.TryGet(request.TextDocument.Uri, out var text))
                return Task.FromResult(HoverHandler.Handle(text, request.Position, manager.Schema));

            return Task.FromResult<Hover?>(null);
        };

        Func<DefinitionParams, CancellationToken, Task<LocationOrLocationLinks?>> onDefinition = (request, _) =>
        {
            if (manager is { IsEngineWorkspace: true } && documentStore.TryGet(request.TextDocument.Uri, out var text))
                return Task.FromResult(DefinitionHandler.Handle(text, request.Position, manager.Schema));

            return Task.FromResult<LocationOrLocationLinks?>(null);
        };

        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(options => options
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .ConfigureLogging(builder => builder
                .AddProvider(new StderrLoggerProvider())
                .SetMinimumLevel(LogLevel.Trace))
            .OnInitialize(async (server, request, token) =>
            {
                textDocument = server.TextDocument;
                languageServer = server;

                var roots = GetWorkspaceRoots(request);
                manager = new EngineWorkspaceManager(loggerFactory);
                manager.SchemaChanged += RevalidateAllOpenDocuments;
                manager.SchemaChanged += PublishReadyStatus;
                manager.SchemaRebuildStarted += PublishIndexing;

                PublishIndexing();
                await manager.InitializeAsync(roots, token);
            })
            .OnInitialized((server, _, _, _) =>
            {
                if (manager is { IsEngineWorkspace: true })
                {
                    server.Window.LogInfo(
                        $"OIRF Engine LSP: workspace ready - {manager.Schema.PrototypesByTypeKey.Count} prototype type(s), " +
                        $"{manager.Schema.ComponentsByName.Count} component type(s).");
                }
                else
                {
                    server.Window.LogInfo("OIRF Engine LSP: not an engine workspace - prototype features disabled.");
                }

                return Task.CompletedTask;
            })
            .OnDidOpenTextDocument(onDidOpen, (_, _) => new TextDocumentOpenRegistrationOptions { DocumentSelector = PrototypeSelector })
            .OnDidChangeTextDocument(onDidChange, (_, _) => new TextDocumentChangeRegistrationOptions { DocumentSelector = PrototypeSelector, SyncKind = TextDocumentSyncKind.Full })
            .OnDidSaveTextDocument(onDidSave, (_, _) => new TextDocumentSaveRegistrationOptions { DocumentSelector = PrototypeSelector })
            .OnDidCloseTextDocument(onDidClose, (_, _) => new TextDocumentCloseRegistrationOptions { DocumentSelector = PrototypeSelector })
            .OnDidChangeWatchedFiles(onDidChangeWatchedFiles, (_, _) => new DidChangeWatchedFilesRegistrationOptions
            {
                Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(
                    new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher { GlobPattern = "**/*.cs" },
                    new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher { GlobPattern = "**/Textures/**" },
                    new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher { GlobPattern = "**/Shaders/**" }),
            })
            .OnCompletion(onCompletion, (_, _) => new CompletionRegistrationOptions { DocumentSelector = PrototypeSelector })
            .OnHover(onHover, (_, _) => new HoverRegistrationOptions { DocumentSelector = PrototypeSelector })
            .OnDefinition(onDefinition, (_, _) => new DefinitionRegistrationOptions { DocumentSelector = PrototypeSelector })
        );

        await server.WaitForExit;
    }

    /// <summary>Payload for the custom "oirf/status" notification - see client/src/extension.ts.</summary>
    private sealed record StatusParams(string State, int PrototypeCount, int ComponentCount);

    private static List<string> GetWorkspaceRoots(InitializeParams request)
    {
        var roots = new List<string>();

        if (request.WorkspaceFolders is { } folders)
        {
            foreach (var folder in folders)
            {
                var path = folder.Uri.GetFileSystemPath();
                if (!string.IsNullOrEmpty(path))
                    roots.Add(path);
            }
        }

        if (roots.Count == 0 && request.RootUri is { } rootUri)
        {
            var path = rootUri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(path))
                roots.Add(path);
        }

        if (roots.Count == 0 && !string.IsNullOrEmpty(request.RootPath))
            roots.Add(request.RootPath!);

        return roots;
    }
}
