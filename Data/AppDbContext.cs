using Microsoft.EntityFrameworkCore;
using sidequest.backend.Models;

namespace sidequest.backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TripMember> TripMembers => Set<TripMember>();
    public DbSet<TripActivity> TripActivities => Set<TripActivity>();
    public DbSet<TripInvite> TripInvites => Set<TripInvite>();
    public DbSet<ActivityComment> ActivityComments => Set<ActivityComment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpensePayer> ExpensePayers => Set<ExpensePayer>();
    public DbSet<ExpenseParticipant> ExpenseParticipants => Set<ExpenseParticipant>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<TripEvent> TripEvents => Set<TripEvent>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatMessageReaction> ChatMessageReactions => Set<ChatMessageReaction>();
    public DbSet<ChatPresenceEntry> ChatPresence => Set<ChatPresenceEntry>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<PushToken> PushTokens => Set<PushToken>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<PushDeliveryAttempt> PushDeliveryAttempts => Set<PushDeliveryAttempt>();
    public DbSet<UserReport> UserReports => Set<UserReport>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<SupportAttachment> SupportAttachments => Set<SupportAttachment>();
    public DbSet<PackingListCategory> PackingListCategories => Set<PackingListCategory>();
    public DbSet<PackingListItem> PackingListItems => Set<PackingListItem>();
    public DbSet<TripDayLocation> TripDayLocations => Set<TripDayLocation>();
    public DbSet<TripDocument> TripDocuments => Set<TripDocument>();
    public DbSet<UserTravelStats> UserTravelStats => Set<UserTravelStats>();
    public DbSet<GlunoConversation> GlunoConversations => Set<GlunoConversation>();
    public DbSet<GlunoMessage> GlunoMessages => Set<GlunoMessage>();
    public DbSet<GlunoProposalRecord> GlunoProposals => Set<GlunoProposalRecord>();
    public DbSet<GlunoPreference> GlunoPreferences => Set<GlunoPreference>();
    /// One compact working-memory row per conversation — see GlunoWorkingState.
    public DbSet<GlunoConversationState> GlunoConversationStates => Set<GlunoConversationState>();
    /// Idempotency claims for chat sends — see GlunoIdempotencyStore.
    public DbSet<GlunoTurnRequest> GlunoTurnRequests => Set<GlunoTurnRequest>();
    /// Document readings — see GlunoDocumentAnalysisService.
    public DbSet<GlunoDocumentAnalysis> GlunoDocumentAnalyses => Set<GlunoDocumentAnalysis>();
    /// Group decisions and their votes — see GlunoGroupDecisionService.
    public DbSet<GlunoGroupDecision> GlunoGroupDecisions => Set<GlunoGroupDecision>();
    public DbSet<GlunoGroupVote> GlunoGroupVotes => Set<GlunoGroupVote>();
    /// Append-only learning signals — see GlunoFeedbackService.
    public DbSet<GlunoFeedbackEvent> GlunoFeedbackEvents => Set<GlunoFeedbackEvent>();
    public DbSet<GlunoPreferenceCandidate> GlunoPreferenceCandidates => Set<GlunoPreferenceCandidate>();
    public DbSet<GlunoRejection> GlunoRejections => Set<GlunoRejection>();
    /// Clickable follow-up questions — see GlunoClarificationService.
    public DbSet<GlunoClarification> GlunoClarifications => Set<GlunoClarification>();
    public DbSet<GlunoClarificationOption> GlunoClarificationOptions => Set<GlunoClarificationOption>();
    /// Suggestions mid-negotiation — see GlunoProposalDraft.
    public DbSet<GlunoProposalDraft> GlunoProposalDrafts => Set<GlunoProposalDraft>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── Gluno ─────────────────────────────────────────────────────────
        // Conversations die with their owner. They are personal — there is no
        // path that shows one user another user's Gluno history — so keeping
        // them after the account is gone would serve nobody.
        modelBuilder.Entity<GlunoConversation>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A trip-scoped conversation survives its Adventure being deleted, as
        // a global one: SetNull rather than Cascade, because what the user
        // asked and what Gluno answered is still theirs. The context builder
        // already treats a missing trip as "no Adventure selected".
        modelBuilder.Entity<GlunoConversation>()
            .HasOne(c => c.Trip)
            .WithMany()
            .HasForeignKey(c => c.TripId)
            .OnDelete(DeleteBehavior.SetNull);

        // The conversation list query: this user's, newest first.
        modelBuilder.Entity<GlunoConversation>()
            .HasIndex(c => new { c.UserId, c.UpdatedAt });

        modelBuilder.Entity<GlunoConversation>()
            .HasIndex(c => new { c.UserId, c.TripId });

        modelBuilder.Entity<GlunoConversation>()
            .Property(c => c.Title)
            .HasMaxLength(120);

        modelBuilder.Entity<GlunoMessage>()
            .HasOne(m => m.Conversation)
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every read of a conversation is "its messages, oldest first".
        modelBuilder.Entity<GlunoMessage>()
            .HasIndex(m => new { m.ConversationId, m.CreatedAt });

        modelBuilder.Entity<GlunoMessage>()
            .Property(m => m.Role)
            .HasMaxLength(20);

        modelBuilder.Entity<GlunoMessage>()
            .Property(m => m.ToolName)
            .HasMaxLength(60);

        modelBuilder.Entity<GlunoMessage>()
            .Property(m => m.ToolCallId)
            .HasMaxLength(80);

        // Proposals die with their conversation — a proposal without the
        // exchange that produced it is unreviewable, so keeping it would only
        // leave an un-auditable pending change behind.
        modelBuilder.Entity<GlunoProposalRecord>()
            .HasOne(p => p.Conversation)
            .WithMany()
            .HasForeignKey(p => p.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoProposalRecord>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting the Adventure leaves the proposal readable as history; it
        // can no longer be applied, because apply re-resolves the trip and
        // finds nothing.
        modelBuilder.Entity<GlunoProposalRecord>()
            .HasOne(p => p.Trip)
            .WithMany()
            .HasForeignKey(p => p.TripId)
            .OnDelete(DeleteBehavior.SetNull);

        // The chat's own lookup: every proposal attached to a rendered turn.
        modelBuilder.Entity<GlunoProposalRecord>()
            .HasIndex(p => p.MessageId);

        modelBuilder.Entity<GlunoProposalRecord>()
            .HasIndex(p => new { p.UserId, p.Status });

        modelBuilder.Entity<GlunoProposalRecord>()
            .Property(p => p.ActionType)
            .HasMaxLength(60);

        modelBuilder.Entity<GlunoProposalRecord>()
            .Property(p => p.Status)
            .HasMaxLength(20);

        modelBuilder.Entity<GlunoProposalRecord>()
            .Property(p => p.FailureCode)
            .HasMaxLength(60);

        modelBuilder.Entity<GlunoProposalRecord>()
            .Property(p => p.Summary)
            .HasMaxLength(300);

        // Preferences die with their user, and a conversation-scoped one dies
        // with its conversation — "forget this chat" has to mean the things
        // said in it are gone too.
        modelBuilder.Entity<GlunoPreference>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoPreference>()
            .HasIndex(p => new { p.UserId, p.Key });

        modelBuilder.Entity<GlunoPreference>()
            .HasIndex(p => new { p.UserId, p.ConversationId });

        modelBuilder.Entity<GlunoPreference>()
            .HasIndex(p => new { p.UserId, p.TripId });

        modelBuilder.Entity<GlunoPreference>()
            .Property(p => p.Key)
            .HasMaxLength(40);

        modelBuilder.Entity<GlunoPreference>()
            .Property(p => p.Scope)
            .HasMaxLength(20);

        modelBuilder.Entity<GlunoPreference>()
            .Property(p => p.Value)
            .HasMaxLength(240);

        // Working memory: exactly one row per conversation, and it goes when
        // the conversation goes. Nothing in it outlives the chat it belongs to.
        modelBuilder.Entity<GlunoConversationState>()
            .HasIndex(s => s.ConversationId)
            .IsUnique();

        modelBuilder.Entity<GlunoConversationState>()
            .HasOne<GlunoConversation>()
            .WithMany()
            .HasForeignKey(s => s.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The unique index IS the idempotency guarantee. Two concurrent sends
        // with the same key race here, and the database decides — a read-then-
        // write check in application code would let both through.
        modelBuilder.Entity<GlunoTurnRequest>()
            .HasIndex(r => new { r.UserId, r.ConversationId, r.IdempotencyKey })
            .IsUnique();

        modelBuilder.Entity<GlunoTurnRequest>()
            .HasOne<GlunoConversation>()
            .WithMany()
            .HasForeignKey(r => r.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting the document deletes its readings. An extraction is derived
        // from a file, and keeping it after the file is gone leaves a record of
        // somebody's booking that they believe they removed.
        modelBuilder.Entity<GlunoDocumentAnalysis>()
            .HasOne(a => a.Document)
            .WithMany()
            .HasForeignKey(a => a.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // The person who asked for the analysis. Without this the row survives
        // their account: an orphaned UserId next to the flight numbers, hotel
        // names and booking references read out of their documents. Trip
        // deletion is already covered by the document cascade above.
        modelBuilder.Entity<GlunoDocumentAnalysis>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The dedupe lookup: "have we already read these exact bytes?"
        modelBuilder.Entity<GlunoDocumentAnalysis>()
            .HasIndex(a => new { a.DocumentId, a.SourceFileHash });

        modelBuilder.Entity<GlunoDocumentAnalysis>()
            .HasIndex(a => new { a.TripId, a.Status });

        modelBuilder.Entity<GlunoGroupDecision>()
            .HasOne(d => d.Trip)
            .WithMany()
            .HasForeignKey(d => d.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoGroupDecision>()
            .HasIndex(d => new { d.TripId, d.Status });

        // Optimistic concurrency on the decision row: two people resolving a
        // poll at the same moment must not silently overwrite each other.
        modelBuilder.Entity<GlunoGroupDecision>()
            .Property(d => d.RowVersion)
            .IsRowVersion();

        // ONE vote per member per decision, enforced by the database. Changing
        // a vote updates the row; it never adds a second.
        modelBuilder.Entity<GlunoGroupVote>()
            .HasIndex(v => new { v.DecisionId, v.UserId })
            .IsUnique();

        modelBuilder.Entity<GlunoGroupVote>()
            .HasOne(v => v.Decision)
            .WithMany()
            .HasForeignKey(v => v.DecisionId)
            .OnDelete(DeleteBehavior.Cascade);

        // ── Learning signals ──────────────────────────────────────────────
        //
        // All three cascade with the USER. Account deletion must take every
        // feedback row, candidate and rejection with it — this is somebody's
        // record of what they liked and turned down, and it has no reason to
        // survive them.
        modelBuilder.Entity<GlunoFeedbackEvent>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting an Adventure takes its trip-scoped feedback with it.
        modelBuilder.Entity<GlunoFeedbackEvent>()
            .HasOne<Trip>()
            .WithMany()
            .HasForeignKey(e => e.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoFeedbackEvent>()
            .HasIndex(e => new { e.UserId, e.EventType });

        modelBuilder.Entity<GlunoFeedbackEvent>()
            .HasIndex(e => new { e.MessageId, e.SupersededAt });

        modelBuilder.Entity<GlunoPreferenceCandidate>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoPreferenceCandidate>()
            .HasOne<Trip>()
            .WithMany()
            .HasForeignKey(c => c.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        // The observation lookup: one live candidate per user, key and trip.
        modelBuilder.Entity<GlunoPreferenceCandidate>()
            .HasIndex(c => new { c.UserId, c.Key, c.TripId, c.Status });

        modelBuilder.Entity<GlunoRejection>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoRejection>()
            .HasOne<Trip>()
            .WithMany()
            .HasForeignKey(r => r.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoRejection>()
            .HasIndex(r => new { r.UserId, r.TripId, r.ExpiresAt });

        // ── Clarifications ────────────────────────────────────────────────
        //
        // A question dies with its conversation: it is only answerable in the
        // exchange that asked it, and a dangling clarification would offer a
        // continuation with nothing to continue.
        modelBuilder.Entity<GlunoClarification>()
            .HasOne(c => c.Conversation)
            .WithMany()
            .HasForeignKey(c => c.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoClarification>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting the Adventure leaves the question readable but no longer
        // answerable — the membership re-check at resolve time finds nothing.
        modelBuilder.Entity<GlunoClarification>()
            .HasOne<Trip>()
            .WithMany()
            .HasForeignKey(c => c.TripId)
            .OnDelete(DeleteBehavior.SetNull);

        // The chat's lookup: the open question for this conversation.
        modelBuilder.Entity<GlunoClarification>()
            .HasIndex(c => new { c.ConversationId, c.Status });

        modelBuilder.Entity<GlunoClarification>()
            .HasIndex(c => c.MessageId);

        modelBuilder.Entity<GlunoClarificationOption>()
            .HasOne(o => o.Clarification)
            .WithMany(c => c.Options)
            .HasForeignKey(o => o.ClarificationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One key per clarification: the key is what the client sends back, so
        // two options sharing one would make the choice ambiguous.
        modelBuilder.Entity<GlunoClarificationOption>()
            .HasIndex(o => new { o.ClarificationId, o.OptionKey })
            .IsUnique();

        // ── Proposal drafts ───────────────────────────────────────────────
        //
        // A draft dies with its conversation and its owner: it is a
        // half-finished negotiation, meaningless without the exchange that
        // produced it.
        modelBuilder.Entity<GlunoProposalDraft>()
            .HasOne(d => d.Conversation)
            .WithMany()
            .HasForeignKey(d => d.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoProposalDraft>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting the Adventure takes the draft with it, unlike a proposal.
        // A proposal is history worth keeping; a draft is a suggestion that
        // was never agreed, about a trip that no longer exists.
        modelBuilder.Entity<GlunoProposalDraft>()
            .HasOne<Trip>()
            .WithMany()
            .HasForeignKey(d => d.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GlunoProposalDraft>()
            .HasIndex(d => new { d.ConversationId, d.Status });

        modelBuilder.Entity<TripEvent>()
            .HasOne(e => e.Trip)
            .WithMany()
            .HasForeignKey(e => e.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.Trip)
            .WithMany()
            .HasForeignKey(m => m.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatPresenceEntry>()
            .HasKey(cp => new { cp.TripId, cp.UserId });

        // A day may hold SEVERAL ordered locations, so uniqueness moved from
        // "one row per date" to "one row per position within a date". That is
        // what keeps SortIndex contiguous and unambiguous at the DB level
        // rather than only in application code — two rows can never both claim
        // to be the day's main location.
        modelBuilder.Entity<TripDayLocation>()
            .HasIndex(d => new { d.TripId, d.StartDate, d.SortIndex })
            .IsUnique();

        // Without this, EF's convention-based FK discovery doesn't match
        // CreatedByUserId to the CreatedBy navigation (it looks for
        // CreatedById) and silently adds a second, shadow FK column instead
        // of reusing the explicit one.
        modelBuilder.Entity<TripDayLocation>()
            .HasOne(d => d.CreatedBy)
            .WithMany()
            .HasForeignKey(d => d.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // One aggregate travel-stats row per user; dies with the account.
        modelBuilder.Entity<UserTravelStats>()
            .HasKey(s => s.UserId);
        modelBuilder.Entity<UserTravelStats>()
            .HasOne(s => s.User)
            .WithOne()
            .HasForeignKey<UserTravelStats>(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripDocument>()
            .HasOne(d => d.Trip)
            .WithMany()
            .HasForeignKey(d => d.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripDocument>()
            .HasOne(d => d.CreatedBy)
            .WithMany()
            .HasForeignKey(d => d.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TripDocument>()
            .HasOne(d => d.Activity)
            .WithMany()
            .HasForeignKey(d => d.ActivityId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<TripDocument>()
            .HasIndex(d => d.StoragePath)
            .IsUnique();

        modelBuilder.Entity<TripDocument>()
            .HasIndex(d => new { d.TripId, d.UploadedAt });

        modelBuilder.Entity<TripDocument>()
            .Property(d => d.Name)
            .HasMaxLength(200);

        modelBuilder.Entity<TripDocument>()
            .Property(d => d.Category)
            .HasMaxLength(40);

        modelBuilder.Entity<TripDocument>()
            .Property(d => d.FileType)
            .HasMaxLength(100);

        modelBuilder.Entity<TripDocument>()
            .Property(d => d.StoragePath)
            .HasMaxLength(300);

        modelBuilder.Entity<TripDocument>()
            .Property(d => d.Note)
            .HasMaxLength(2000);

        modelBuilder.Entity<TripDocument>()
            .Property(d => d.BookingReference)
            .HasMaxLength(200);

        modelBuilder.Entity<ChatMessageReaction>()
            .HasOne(r => r.ChatMessage)
            .WithMany()
            .HasForeignKey(r => r.ChatMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        // One reaction per user+emoji per message — toggling is delete/insert.
        modelBuilder.Entity<ChatMessageReaction>()
            .HasIndex(r => new { r.ChatMessageId, r.UserId, r.Emoji })
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<TripMember>()
            .HasIndex(tm => new { tm.TripId, tm.UserId })
            .IsUnique();

        modelBuilder.Entity<TripInvite>()
            .HasIndex(ti => new { ti.TripId, ti.Email })
            .IsUnique();

        modelBuilder.Entity<Trip>()
            .HasOne(t => t.Owner)
            .WithMany()
            .HasForeignKey(t => t.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TripMember>()
            .HasOne(tm => tm.Trip)
            .WithMany()
            .HasForeignKey(tm => tm.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripMember>()
            .HasOne(tm => tm.User)
            .WithMany()
            .HasForeignKey(tm => tm.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripActivity>()
            .HasOne(a => a.Trip)
            .WithMany()
            .HasForeignKey(a => a.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripActivity>()
            .HasOne(a => a.Owner)
            .WithMany()
            .HasForeignKey(a => a.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TripActivity>()
            .HasOne(a => a.AssignedTo)
            .WithMany()
            .HasForeignKey(a => a.AssignedToUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        modelBuilder.Entity<TripInvite>()
            .HasOne(ti => ti.Trip)
            .WithMany()
            .HasForeignKey(ti => ti.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripInvite>()
            .HasOne(ti => ti.InvitedByUser)
            .WithMany()
            .HasForeignKey(ti => ti.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ActivityComment>()
            .HasOne(c => c.Activity)
            .WithMany()
            .HasForeignKey(c => c.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ActivityComment>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Expense>()
            .HasOne(e => e.Trip)
            .WithMany()
            .HasForeignKey(e => e.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Expense>()
            .HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpensePayer>()
            .HasOne(ep => ep.Expense)
            .WithMany(e => e.Payers)
            .HasForeignKey(ep => ep.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExpensePayer>()
            .HasOne(ep => ep.User)
            .WithMany()
            .HasForeignKey(ep => ep.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseParticipant>()
            .HasOne(ep => ep.Expense)
            .WithMany(e => e.Participants)
            .HasForeignKey(ep => ep.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExpenseParticipant>()
            .HasOne(ep => ep.User)
            .WithMany()
            .HasForeignKey(ep => ep.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Settlement>()
            .HasOne(s => s.Trip)
            .WithMany()
            .HasForeignKey(s => s.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Settlement>()
            .HasOne(s => s.FromUser)
            .WithMany()
            .HasForeignKey(s => s.FromUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Settlement>()
            .HasOne(s => s.ToUser)
            .WithMany()
            .HasForeignKey(s => s.ToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PushToken>()
            .HasOne(pt => pt.User)
            .WithMany()
            .HasForeignKey(pt => pt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per (user, token) — re-registering the same device just
        // updates LastSeenAt/IsActive instead of creating a duplicate.
        modelBuilder.Entity<PushToken>()
            .HasIndex(pt => new { pt.UserId, pt.Token })
            .IsUnique();

        // The actual idempotency guard: a unique index on DedupeKey means
        // even a race between two scheduler ticks can't double-insert, since
        // the second insert fails at the database level, not just in code.
        modelBuilder.Entity<NotificationLog>()
            .HasIndex(n => n.DedupeKey)
            .IsUnique();

        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasOne(a => a.NotificationLog)
            .WithMany(n => n.Attempts)
            .HasForeignKey(a => a.NotificationLogId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasOne(a => a.PushToken)
            .WithMany()
            .HasForeignKey(a => a.PushTokenId)
            .OnDelete(DeleteBehavior.Cascade);

        // The scheduler's "find due retries" query filters on (Status,
        // NextAttemptAt) every tick — index it.
        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasIndex(a => new { a.Status, a.NextAttemptAt });

        // The receipt-checker's "find accepted tickets to verify" query.
        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasIndex(a => a.Status);

        modelBuilder.Entity<UserReport>()
            .HasOne(r => r.Reporter)
            .WithMany()
            .HasForeignKey(r => r.ReporterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserBlock>()
            .HasOne(b => b.Blocker)
            .WithMany()
            .HasForeignKey(b => b.BlockerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserBlock>()
            .HasOne(b => b.BlockedUser)
            .WithMany()
            .HasForeignKey(b => b.BlockedUserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserBlock>()
            .HasIndex(b => new { b.BlockerId, b.BlockedUserId })
            .IsUnique();

        modelBuilder.Entity<SupportTicket>()
            .HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportMessage>()
            .HasOne(m => m.Ticket)
            .WithMany(t => t.Messages)
            .HasForeignKey(m => m.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportAttachment>()
            .HasOne(a => a.Message)
            .WithMany(m => m.Attachments)
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportTicket>()
            .HasIndex(t => t.UserId);

        modelBuilder.Entity<SupportTicket>()
            .HasIndex(t => t.Status);
    }
}
