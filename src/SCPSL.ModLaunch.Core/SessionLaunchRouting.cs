namespace SCPSL.ModLaunch.Core;

/// <summary>Honors the requested loader mode when an earlier session is still active.</summary>
public static class SessionLaunchRouting
{
    public static bool CanReuse(LaunchArguments arguments, SessionJournal session) =>
        arguments.NoRestore && !arguments.Repair && session.SkipMelonLoader == arguments.NoMelonLoader;

    public static bool ShouldPrepareAfterRestoration(LaunchArguments arguments, SessionJournal session) =>
        arguments.Launch || arguments.Repair ||
        (arguments.NoRestore && session.SkipMelonLoader != arguments.NoMelonLoader);
}
