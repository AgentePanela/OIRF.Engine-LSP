using Microsoft.Build.Locator;

// MSBuildLocator.RegisterDefaults() MUST run before any type from Microsoft.CodeAnalysis.MSBuild
// or Microsoft.Build.* is touched anywhere in the process - that's why LanguageServerHost (which
// owns the Roslyn/MSBuildWorkspace code) is only constructed after this call, never before.
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

await OIRF.LanguageServer.LanguageServerHost.RunAsync(args);
