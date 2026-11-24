using Microsoft.Build.Locator;

namespace OIRF.LanguageServer.Workspace;

public static class MSBuildAvailability
{
    public static bool IsAvailable => MSBuildLocator.IsRegistered;
    public static string? FailureReason { get; private set; }

    public const string UserFacingWarning =
        "OIRF Engine LSP: no .NET SDK / MSBuild was found, so C# prototype and component definitions can't be read. " +
        "Prototype validation and completion are disabled; info.yml support still works. " +
        "Install the .NET SDK (https://dotnet.microsoft.com/download) and restart VS Code to enable everything.";

    public static void Register()
    {
        if (MSBuildLocator.IsRegistered)
            return;

        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch (InvalidOperationException ex)
        {
            FailureReason = ex.Message;
        }
    }
}
