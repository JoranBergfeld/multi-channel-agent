using System.Runtime.CompilerServices;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Points ASP.NET Core's buffering directory at a path that does not exist, once, before any test in
/// this assembly runs - so that a request which spills an uploaded body to disk fails immediately and
/// visibly instead of succeeding quietly.
///
/// A test cannot prove the absence of a temp file by looking for one afterwards:
/// <c>FileBufferingReadStream</c> creates its file with <c>FileOptions.DeleteOnClose</c> and the
/// request disposes it, so the directory is empty either way. Denying the directory turns the same
/// question into a deterministic answer, and it cannot be disarmed by test ordering:
/// <c>AspNetCoreTempDirectory</c> resolves the path only when a stream is about to create the file and
/// caches it only when it exists, so a missing directory is re-resolved - and refused - every time.
///
/// Nothing else reads this variable: only the form and response buffering streams do, and the raw
/// Initial Import upload is the one body in this system large enough to reach them. It lives in memory
/// for the length of its request and in SQL while its proposal is pending, and nowhere else.
/// </summary>
internal static class UploadSpillGuard
{
    [ModuleInitializer]
    internal static void Arm() => Environment.SetEnvironmentVariable(
        "ASPNETCORE_TEMP",
        Path.Combine(AppContext.BaseDirectory, "no-request-may-buffer-a-body-to-disk"));
}
