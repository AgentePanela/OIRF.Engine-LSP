using OIRF.LanguageServer.Workspace;

// MSBuild registration MUST run before any type from Microsoft.CodeAnalysis.MSBuild or
// Microsoft.Build.* is touched anywhere in the process - that's why LanguageServerHost (which
// owns the Roslyn/MSBuildWorkspace code) is only constructed after this call, never before.
// A missing MSBuild is not fatal: the server runs in a reduced mode (see MSBuildAvailability).
MSBuildAvailability.Register();

await OIRF.LanguageServer.LanguageServerHost.RunAsync(args);
