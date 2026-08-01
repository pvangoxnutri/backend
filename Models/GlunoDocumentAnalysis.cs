using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Models;

public static class GlunoDocumentAnalysisStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    /// The document changed. This result describes a file that no longer exists.
    public const string Superseded = "superseded";

    public static readonly IReadOnlyList<string> Terminal =
        [Completed, Failed, Cancelled, Superseded];

    public static bool IsTerminal(string status) => Terminal.Contains(status);
}

/// <summary>
/// One reading of one version of one document.
///
/// WHY THE HASH IS THE IDENTITY. A document can be re-uploaded, renamed, or
/// replaced with a corrected version, and the row has to know which of those
/// happened. The file's SHA-256 answers all three: same hash means the same
/// bytes and the existing analysis stands; a different hash means a genuinely
/// new document, which gets a new analysis and SUPERSEDES the old one — because
/// a result describing a file the user has replaced is worse than no result.
///
/// WHAT IS DELIBERATELY NOT STORED. The document text. The OCR output. The
/// provider's raw response. Those are a second copy of somebody's flight
/// tickets living in a database, and the product needs the structured result,
/// not the source. <see cref="RawTextExcerpt"/> exists only for deployments
/// that explicitly opt in.
/// </summary>
public class GlunoDocumentAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentId { get; set; }
    public TripDocument Document { get; set; } = null!;

    /// Denormalised from the document so authorisation can be checked without
    /// a join, and so the row survives a document delete long enough to be
    /// cleaned up deliberately.
    public Guid TripId { get; set; }

    /// Who asked for the analysis.
    public Guid UserId { get; set; }

    /// The extraction schema this result was produced under.
    public int ExtractionVersion { get; set; } = 1;

    /// <see cref="GlunoDocumentAnalysisStatuses"/>.
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = GlunoDocumentAnalysisStatuses.Pending;

    /// The validated <see cref="Services.Gluno.GlunoDocumentExtraction"/>, as
    /// JSON. Minimal by design — see the class comment.
    public string? StructuredResultJson { get; set; }

    /// <summary>
    /// SHA-256 of the analysed bytes.
    ///
    /// Idempotency and dedupe both hang off this. Never the file name, which
    /// the user controls and which changes for reasons that have nothing to do
    /// with the content.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string SourceFileHash { get; set; } = string.Empty;

    /// A stable failure code. Never a provider message.
    [MaxLength(40)]
    public string? FailureCode { get; set; }

    /// Which model produced this, for reproducibility. Server-side only.
    [MaxLength(80)]
    public string? ProviderModel { get; set; }

    /// <summary>
    /// Only set for deployments that opted into keeping text. Capped hard —
    /// this is an excerpt for debugging, never the document.
    /// </summary>
    [MaxLength(2000)]
    public string? RawTextExcerpt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When the user actually looked at the result.
    ///
    /// Load-bearing: nothing from a document may enter Gluno's Adventure
    /// context until a human has read it. An extraction is a machine's reading
    /// of a photograph, and it becomes a fact about the trip only once its
    /// owner agrees.
    /// </summary>
    public DateTime? UserReviewedAt { get; set; }

    /// Set when a newer analysis of the same document replaced this one.
    public DateTime? SupersededAt { get; set; }
}
