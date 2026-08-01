namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Removes temporary document files a crashed analysis left behind.
///
/// WHY THIS EXISTS EVEN THOUGH THE HAPPY PATH CLEANS UP. The happy path always
/// cleans up. The interesting cases are the other ones: a process killed
/// mid-analysis, a host restarted during a deploy, an unhandled exception in a
/// path nobody anticipated. Each leaves a file on disk, and that file is
/// somebody's flight ticket or hotel confirmation.
///
/// A leaked temporary file is not a disk-space problem. It is a private
/// document sitting outside the storage system that was designed to protect it,
/// with none of that system's access control, for as long as nobody notices —
/// which, without a sweeper, is forever.
///
/// Deliberately conservative about what it touches: only files matching the
/// server-generated prefix, only inside the analysis directory, only past the
/// retention window. It never deletes anything it did not create.
/// </summary>
public sealed class GlunoDocumentTempSweeper : BackgroundService
{
    /// <summary>
    /// The prefix every temporary analysis file carries — see
    /// GlunoDocumentFile.TemporaryPath. Matching on it means an unrelated file
    /// that happens to sit in this directory is never removed.
    /// </summary>
    private const string FilePrefix = "gluno-";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private readonly GlunoDocumentConfig _config;
    private readonly ILogger<GlunoDocumentTempSweeper> _logger;

    public GlunoDocumentTempSweeper(GlunoDocumentConfig config, ILogger<GlunoDocumentTempSweeper> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Where working files live. A dedicated subdirectory rather than the
    /// system temp root, so the sweeper's scope is bounded and nothing else
    /// shares it.
    /// </summary>
    public static string Directory => Path.Combine(Path.GetTempPath(), "sidequest-gluno-documents");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once at startup: the files most worth removing are exactly the ones a
        // previous process left behind, and waiting ten minutes to clear them
        // is ten minutes of a private document sitting unprotected.
        Sweep();

        using var timer = new PeriodicTimer(SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Nothing to report.
        }
    }

    private void Sweep()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            var cutoff = DateTime.UtcNow - _config.TemporaryFileRetention;
            var removed = 0;

            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) > cutoff) continue;

                    File.Delete(path);
                    removed++;
                }
                catch (IOException)
                {
                    // Still open — an analysis is using it. It will be caught
                    // on the next pass.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same: try again later rather than escalating.
                }
            }

            // A count only. A filename could carry a document's original name,
            // and a document's name is often the booking it belongs to.
            if (removed > 0) _logger.LogInformation("[GLUNO] swept {Count} temporary document files", removed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[GLUNO] temporary file sweep failed: {Category}", ex.GetType().Name);
        }
    }
}
