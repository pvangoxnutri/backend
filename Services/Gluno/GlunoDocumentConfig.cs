namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Server-side configuration for document analysis.
///
/// OFF by default, like every other external integration in this codebase.
/// Shipping the code must not start reading people's booking confirmations —
/// that decision is made deliberately, per environment, or not at all.
///
/// Nothing here reaches the mobile app. The app learns one boolean: whether
/// analysis is available.
/// </summary>
public sealed class GlunoDocumentConfig
{
    private readonly IConfiguration _config;
    private readonly GlunoModelPolicy _models;

    public GlunoDocumentConfig(IConfiguration config, GlunoModelPolicy models)
    {
        _config = config;
        _models = models;
    }

    /// <summary>
    /// Off unless explicitly switched on AND a model is configured.
    ///
    /// A deployment with the flag on but no model reports "not_configured"
    /// rather than failing per document — the app then hides the action
    /// instead of offering a button that always errors.
    /// </summary>
    public bool IsEnabled
        => _config.GetValue("Gluno:Documents:Enabled", false) && Model != null;

    public string? UnavailableReason
        => !_config.GetValue("Gluno:Documents:Enabled", false) ? "disabled"
         : Model == null ? "not_configured"
         : null;

    /// <summary>
    /// The model that reads documents. Falls back to Gluno's primary, so one
    /// setting is enough — but can be pinned separately, because document
    /// reading and conversation are genuinely different jobs.
    /// </summary>
    public string? Model
    {
        get
        {
            var explicitModel = _config["Gluno:Documents:Model"];
            if (!string.IsNullOrWhiteSpace(explicitModel)) return explicitModel.Trim();

            return _models.IsConfigured
                ? _models.Choose(new GlunoModelRequest
                {
                    Intent = GlunoIntent.GeneralTravelQuestion,
                    IntentConfidence = 1,
                }).Model
                : null;
        }
    }

    /// <summary>
    /// Bytes. Bounded because the file is read into memory and sent onward —
    /// an unbounded upload is both a memory problem and a bill.
    /// </summary>
    public long MaxFileSizeBytes => Math.Clamp(
        _config.GetValue<long>("Gluno:Documents:MaxFileSizeBytes", 10L * 1024 * 1024),
        64 * 1024,
        50L * 1024 * 1024);

    /// <summary>
    /// Pages read. A booking confirmation is one to three pages; a 200-page
    /// PDF is a different kind of document and reading all of it would cost
    /// far more than the answer is worth.
    /// </summary>
    public int MaxPages => Math.Clamp(_config.GetValue("Gluno:Documents:MaxPages", 8), 1, 30);

    public int MaxImages => Math.Clamp(_config.GetValue("Gluno:Documents:MaxImages", 4), 1, 10);

    public TimeSpan Timeout => TimeSpan.FromSeconds(
        Math.Clamp(_config.GetValue("Gluno:Documents:TimeoutSeconds", 90), 10, 300));

    /// A runaway backstop, not a plan tier.
    public int DailyPerUserLimit
        => Math.Max(1, _config.GetValue("Gluno:Documents:DailyPerUserLimit", 30));

    public int GlobalDailyLimit
        => Math.Max(1, _config.GetValue("Gluno:Documents:GlobalDailyLimit", 2000));

    /// <summary>
    /// How long a temporary working file may exist before the sweeper removes
    /// it.
    ///
    /// Deliberately short. These files are somebody's flight tickets and hotel
    /// confirmations; the correct lifetime is "as long as the analysis takes"
    /// and this window only exists to catch a process that died mid-run.
    /// </summary>
    public TimeSpan TemporaryFileRetention => TimeSpan.FromMinutes(
        Math.Clamp(_config.GetValue("Gluno:Documents:TemporaryFileRetentionMinutes", 15), 1, 240));

    /// <summary>
    /// Whether the extracted raw text may be kept on the analysis row.
    ///
    /// Off by default. The structured result is what the product needs; the
    /// full text is a second copy of a private document sitting in a database,
    /// and keeping it by default is the kind of decision nobody revisits.
    /// </summary>
    public bool StoreRawText => _config.GetValue("Gluno:Documents:StoreRawText", false);
}
