CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330193407_InitialCreate') THEN
    CREATE TABLE "Trips" (
        "Id" uuid NOT NULL,
        "Title" text NOT NULL,
        "Description" text,
        "Destination" text NOT NULL,
        "StartDate" date NOT NULL,
        "EndDate" date NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Trips" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330193407_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260330193407_InitialCreate', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    ALTER TABLE "Trips" ADD "OwnerId" uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    CREATE TABLE "Users" (
        "Id" uuid NOT NULL,
        "Name" text NOT NULL,
        "Email" text NOT NULL,
        "PasswordHash" text NOT NULL,
        "AvatarUrl" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Users" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    CREATE TABLE "TripMembers" (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "JoinedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_TripMembers" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_TripMembers_Trips_TripId" FOREIGN KEY ("TripId") REFERENCES "Trips" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_TripMembers_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    CREATE INDEX "IX_Trips_OwnerId" ON "Trips" ("OwnerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    CREATE UNIQUE INDEX "IX_TripMembers_TripId_UserId" ON "TripMembers" ("TripId", "UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    CREATE INDEX "IX_TripMembers_UserId" ON "TripMembers" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    ALTER TABLE "Trips" ADD CONSTRAINT "FK_Trips_Users_OwnerId" FOREIGN KEY ("OwnerId") REFERENCES "Users" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260330205901_AddUsersAndAuth') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260330205901_AddUsersAndAuth', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260331124152_AddTripActivities') THEN
    CREATE TABLE "TripActivities" (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "Date" date NOT NULL,
        "Title" text NOT NULL,
        "Description" text,
        "Time" text,
        "Category" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_TripActivities" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_TripActivities_Trips_TripId" FOREIGN KEY ("TripId") REFERENCES "Trips" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260331124152_AddTripActivities') THEN
    CREATE INDEX "IX_TripActivities_TripId" ON "TripActivities" ("TripId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260331124152_AddTripActivities') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260331124152_AddTripActivities', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401132708_UpdatePendingChanges') THEN
    ALTER TABLE "TripActivities" ADD "AssignedToUserId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401132708_UpdatePendingChanges') THEN
    ALTER TABLE "TripActivities" ADD "IsHidden" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401132708_UpdatePendingChanges') THEN
    CREATE INDEX "IX_TripActivities_AssignedToUserId" ON "TripActivities" ("AssignedToUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401132708_UpdatePendingChanges') THEN
    ALTER TABLE "TripActivities" ADD CONSTRAINT "FK_TripActivities_Users_AssignedToUserId" FOREIGN KEY ("AssignedToUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401132708_UpdatePendingChanges') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260401132708_UpdatePendingChanges', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401150000_AddHiddenSideQuestFields') THEN
    ALTER TABLE "Users" ADD "Role" text NOT NULL DEFAULT 'user';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401150000_AddHiddenSideQuestFields') THEN
    ALTER TABLE "Trips" ADD "Visibility" text NOT NULL DEFAULT 'public';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401150000_AddHiddenSideQuestFields') THEN
    ALTER TABLE "Trips" ADD "RevealAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401150000_AddHiddenSideQuestFields') THEN
    ALTER TABLE "Trips" ADD "Teaser" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401150000_AddHiddenSideQuestFields') THEN
    ALTER TABLE "TripMembers" ADD "IsOwner" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401150000_AddHiddenSideQuestFields') THEN
    UPDATE "TripMembers" tm
    SET "IsOwner" = true
    FROM "Trips" t
    WHERE tm."TripId" = t."Id" AND tm."UserId" = t."OwnerId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260401150000_AddHiddenSideQuestFields') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260401150000_AddHiddenSideQuestFields', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402053307_SyncSupabaseAuth') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260402053307_SyncSupabaseAuth', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402113000_AddTripInvitesAndCodes') THEN
    ALTER TABLE "Trips" ADD "InviteCode" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402113000_AddTripInvitesAndCodes') THEN
    UPDATE "Trips"
    SET "InviteCode" = UPPER(SUBSTRING(REPLACE(CAST("Id" AS text), '-', '') FROM 1 FOR 6))
    WHERE COALESCE("InviteCode", '') = '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402113000_AddTripInvitesAndCodes') THEN
    CREATE TABLE "TripInvites" (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "InvitedByUserId" uuid NOT NULL,
        "Email" text NOT NULL,
        "Status" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_TripInvites" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_TripInvites_Trips_TripId" FOREIGN KEY ("TripId") REFERENCES "Trips" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_TripInvites_Users_InvitedByUserId" FOREIGN KEY ("InvitedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402113000_AddTripInvitesAndCodes') THEN
    CREATE INDEX "IX_TripInvites_InvitedByUserId" ON "TripInvites" ("InvitedByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402113000_AddTripInvitesAndCodes') THEN
    CREATE UNIQUE INDEX "IX_TripInvites_TripId_Email" ON "TripInvites" ("TripId", "Email");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402113000_AddTripInvitesAndCodes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260402113000_AddTripInvitesAndCodes', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ADD "ImageUrl" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ADD "OwnerId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ADD "RevealAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ADD "Teaser" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ADD "TeaserOffsetMinutes" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ADD "Visibility" text NOT NULL DEFAULT 'public';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    UPDATE "TripActivities"
    SET "Visibility" = CASE
        WHEN "IsHidden" THEN 'hidden'
        ELSE 'public'
    END;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    UPDATE "TripActivities" AS activity
    SET "OwnerId" = COALESCE(
        activity."AssignedToUserId",
        trip."OwnerId"
    )
    FROM "Trips" AS trip
    WHERE trip."Id" = activity."TripId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ALTER COLUMN "OwnerId" SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    CREATE INDEX "IX_TripActivities_OwnerId" ON "TripActivities" ("OwnerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    ALTER TABLE "TripActivities" ADD CONSTRAINT "FK_TripActivities_Users_OwnerId" FOREIGN KEY ("OwnerId") REFERENCES "Users" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260402115016_AddRichSideQuestActivities') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260402115016_AddRichSideQuestActivities', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260405132343_AddSpotifyUrlToTripActivities') THEN
    ALTER TABLE "TripActivities" ADD "SpotifyUrl" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260405132343_AddSpotifyUrlToTripActivities') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260405132343_AddSpotifyUrlToTripActivities', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260405132810_AddSpotifyUrlToTrips') THEN
    ALTER TABLE "Trips" ADD "SpotifyUrl" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260405132810_AddSpotifyUrlToTrips') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260405132810_AddSpotifyUrlToTrips', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409200000_AddActivityComments') THEN
    CREATE TABLE "ActivityComments" (
        "Id" uuid NOT NULL,
        "ActivityId" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "Text" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ActivityComments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_ActivityComments_TripActivities_ActivityId" FOREIGN KEY ("ActivityId") REFERENCES "TripActivities" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_ActivityComments_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409200000_AddActivityComments') THEN
    CREATE INDEX "IX_ActivityComments_ActivityId" ON "ActivityComments" ("ActivityId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409200000_AddActivityComments') THEN
    CREATE INDEX "IX_ActivityComments_UserId" ON "ActivityComments" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409200000_AddActivityComments') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260409200000_AddActivityComments', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE TABLE "Expenses" (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "CreatedByUserId" uuid NOT NULL,
        "Description" character varying(200) NOT NULL,
        "TotalAmount" numeric NOT NULL,
        "Date" date NOT NULL,
        "SplitMode" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Expenses" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Expenses_Trips_TripId" FOREIGN KEY ("TripId") REFERENCES "Trips" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_Expenses_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE TABLE "ExpensePayers" (
        "Id" uuid NOT NULL,
        "ExpenseId" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "Amount" numeric NOT NULL,
        CONSTRAINT "PK_ExpensePayers" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_ExpensePayers_Expenses_ExpenseId" FOREIGN KEY ("ExpenseId") REFERENCES "Expenses" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_ExpensePayers_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE TABLE "ExpenseParticipants" (
        "Id" uuid NOT NULL,
        "ExpenseId" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "Amount" numeric NOT NULL,
        CONSTRAINT "PK_ExpenseParticipants" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_ExpenseParticipants_Expenses_ExpenseId" FOREIGN KEY ("ExpenseId") REFERENCES "Expenses" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_ExpenseParticipants_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE TABLE "Settlements" (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "FromUserId" uuid NOT NULL,
        "ToUserId" uuid NOT NULL,
        "Amount" numeric NOT NULL,
        "Note" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Settlements" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Settlements_Trips_TripId" FOREIGN KEY ("TripId") REFERENCES "Trips" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_Settlements_Users_FromUserId" FOREIGN KEY ("FromUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_Settlements_Users_ToUserId" FOREIGN KEY ("ToUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_Expenses_TripId" ON "Expenses" ("TripId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_Expenses_CreatedByUserId" ON "Expenses" ("CreatedByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_ExpensePayers_ExpenseId" ON "ExpensePayers" ("ExpenseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_ExpensePayers_UserId" ON "ExpensePayers" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_ExpenseParticipants_ExpenseId" ON "ExpenseParticipants" ("ExpenseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_ExpenseParticipants_UserId" ON "ExpenseParticipants" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_Settlements_TripId" ON "Settlements" ("TripId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_Settlements_FromUserId" ON "Settlements" ("FromUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    CREATE INDEX "IX_Settlements_ToUserId" ON "Settlements" ("ToUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260409210000_AddCostSplit') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260409210000_AddCostSplit', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260508150747_AddTripStatusAndShareCode') THEN
    ALTER TABLE "Trips" ADD "ShareCode" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260508150747_AddTripStatusAndShareCode') THEN
    ALTER TABLE "Trips" ADD "SharedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260508150747_AddTripStatusAndShareCode') THEN
    ALTER TABLE "Trips" ADD "Status" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260508150747_AddTripStatusAndShareCode') THEN
    ALTER TABLE "Expenses" ALTER COLUMN "Description" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260508150747_AddTripStatusAndShareCode') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260508150747_AddTripStatusAndShareCode', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260508164905_AddShareCode') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260508164905_AddShareCode', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513210246_AddFeedback') THEN
    DROP TABLE "Themes";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513210246_AddFeedback') THEN
    ALTER TABLE "Users" DROP COLUMN "ThemeId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513210246_AddFeedback') THEN
    CREATE TABLE "Feedbacks" (
        "Id" text NOT NULL,
        "UserId" text NOT NULL,
        "Email" text NOT NULL,
        "Name" text NOT NULL,
        "Type" text NOT NULL,
        "Message" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Feedbacks" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513210246_AddFeedback') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260513210246_AddFeedback', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513214152_AddFeedbackIsRead') THEN
    ALTER TABLE "Feedbacks" ADD "IsRead" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513214152_AddFeedbackIsRead') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260513214152_AddFeedbackIsRead', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513231908_AddChatMessageImageUrl') THEN
    ALTER TABLE "ChatMessages" ADD "ImageUrl" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260513231908_AddChatMessageImageUrl') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260513231908_AddChatMessageImageUrl', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260517084855_RemoveUserPasswordHash') THEN
    ALTER TABLE "Users" DROP COLUMN "PasswordHash";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260517084855_RemoveUserPasswordHash') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260517084855_RemoveUserPasswordHash', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260606082241_AddRevealedAtToTripActivities') THEN
    ALTER TABLE "TripActivities" ADD "RevealedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260606082241_AddRevealedAtToTripActivities') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260606082241_AddRevealedAtToTripActivities', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260618215123_AddMembersCanEditToTrips') THEN
    ALTER TABLE "Trips" ADD "MembersCanEdit" boolean NOT NULL DEFAULT TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260618215123_AddMembersCanEditToTrips') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260618215123_AddMembersCanEditToTrips', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260620101954_AddReceiptUrlToExpenses') THEN
    ALTER TABLE "Expenses" ADD "ReceiptUrl" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260620101954_AddReceiptUrlToExpenses') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260620101954_AddReceiptUrlToExpenses', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621143257_AddPushNotifications') THEN
    CREATE TABLE "NotificationLogs" (
        "Id" uuid NOT NULL,
        "Type" text NOT NULL,
        "DedupeKey" text NOT NULL,
        "RecipientUserId" uuid NOT NULL,
        "TripId" uuid,
        "Success" boolean NOT NULL,
        "ErrorMessage" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_NotificationLogs" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621143257_AddPushNotifications') THEN
    CREATE TABLE "PushTokens" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "Token" text NOT NULL,
        "Platform" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "LastSeenAt" timestamp with time zone NOT NULL,
        "IsActive" boolean NOT NULL,
        CONSTRAINT "PK_PushTokens" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PushTokens_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621143257_AddPushNotifications') THEN
    CREATE UNIQUE INDEX "IX_NotificationLogs_DedupeKey" ON "NotificationLogs" ("DedupeKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621143257_AddPushNotifications') THEN
    CREATE UNIQUE INDEX "IX_PushTokens_UserId_Token" ON "PushTokens" ("UserId", "Token");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621143257_AddPushNotifications') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260621143257_AddPushNotifications', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    ALTER TABLE "NotificationLogs" DROP COLUMN "ErrorMessage";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    ALTER TABLE "NotificationLogs" DROP COLUMN "Success";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    ALTER TABLE "NotificationLogs" ADD "Body" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    ALTER TABLE "NotificationLogs" ADD "DataJson" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    ALTER TABLE "NotificationLogs" ADD "DeliveredAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    ALTER TABLE "NotificationLogs" ADD "Status" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    ALTER TABLE "NotificationLogs" ADD "Title" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    CREATE TABLE "PushDeliveryAttempts" (
        "Id" uuid NOT NULL,
        "NotificationLogId" uuid NOT NULL,
        "PushTokenId" uuid NOT NULL,
        "Status" text NOT NULL,
        "AttemptCount" integer NOT NULL,
        "LastAttemptAt" timestamp with time zone,
        "NextAttemptAt" timestamp with time zone,
        "ExpoTicketId" text,
        "LastErrorCode" text,
        "DeliveredAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_PushDeliveryAttempts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PushDeliveryAttempts_NotificationLogs_NotificationLogId" FOREIGN KEY ("NotificationLogId") REFERENCES "NotificationLogs" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_PushDeliveryAttempts_PushTokens_PushTokenId" FOREIGN KEY ("PushTokenId") REFERENCES "PushTokens" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    CREATE INDEX "IX_PushDeliveryAttempts_NotificationLogId" ON "PushDeliveryAttempts" ("NotificationLogId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    CREATE INDEX "IX_PushDeliveryAttempts_PushTokenId" ON "PushDeliveryAttempts" ("PushTokenId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    CREATE INDEX "IX_PushDeliveryAttempts_Status" ON "PushDeliveryAttempts" ("Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    CREATE INDEX "IX_PushDeliveryAttempts_Status_NextAttemptAt" ON "PushDeliveryAttempts" ("Status", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260621145610_AddPushDeliveryAttemptsAndOutboxState') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260621145610_AddPushDeliveryAttemptsAndOutboxState', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622182917_AddChatMessageSystemEventType') THEN
    ALTER TABLE "ChatMessages" ADD "SystemEventType" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260622182917_AddChatMessageSystemEventType') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260622182917_AddChatMessageSystemEventType', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623104912_AddLanguageToUsers') THEN
    ALTER TABLE "Users" ADD "Language" text NOT NULL DEFAULT 'en';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623104912_AddLanguageToUsers') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260623104912_AddLanguageToUsers', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623182838_AddSortIndexToTripActivities') THEN
    ALTER TABLE "TripActivities" ADD "SortIndex" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623182838_AddSortIndexToTripActivities') THEN
    UPDATE "TripActivities" AS t
    SET "SortIndex" = ranked.rn
    FROM (
        SELECT "Id", ROW_NUMBER() OVER (
            PARTITION BY "TripId", "Date"
            ORDER BY "Time" NULLS LAST, "CreatedAt"
        ) - 1 AS rn
        FROM "TripActivities"
    ) AS ranked
    WHERE t."Id" = ranked."Id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260623182838_AddSortIndexToTripActivities') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260623182838_AddSortIndexToTripActivities', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626090214_AddActivityIdToTripEvents') THEN
    ALTER TABLE "TripEvents" ADD "ActivityId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626090214_AddActivityIdToTripEvents') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260626090214_AddActivityIdToTripEvents', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626112331_AddIsHiddenToTripEvents') THEN
    ALTER TABLE "TripEvents" ADD "IsHidden" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626112331_AddIsHiddenToTripEvents') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260626112331_AddIsHiddenToTripEvents', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626120656_AddActorAvatarAndActivityTitle') THEN
    ALTER TABLE "TripEvents" ADD "ActivityTitle" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626120656_AddActorAvatarAndActivityTitle') THEN
    ALTER TABLE "NotificationLogs" ADD "ActorAvatarUrl" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626120656_AddActorAvatarAndActivityTitle') THEN
    ALTER TABLE "NotificationLogs" ADD "ActorName" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260626120656_AddActorAvatarAndActivityTitle') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260626120656_AddActorAvatarAndActivityTitle', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701085455_AddReportsAndBlocks') THEN
    CREATE TABLE "UserBlocks" (
        "Id" uuid NOT NULL,
        "BlockerId" uuid NOT NULL,
        "BlockedUserId" uuid NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_UserBlocks" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_UserBlocks_Users_BlockedUserId" FOREIGN KEY ("BlockedUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_UserBlocks_Users_BlockerId" FOREIGN KEY ("BlockerId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701085455_AddReportsAndBlocks') THEN
    CREATE TABLE "UserReports" (
        "Id" uuid NOT NULL,
        "ReporterId" uuid NOT NULL,
        "ContentType" text NOT NULL,
        "ContentId" text NOT NULL,
        "Reason" text NOT NULL,
        "Notes" text,
        "Status" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_UserReports" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_UserReports_Users_ReporterId" FOREIGN KEY ("ReporterId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701085455_AddReportsAndBlocks') THEN
    CREATE INDEX "IX_UserBlocks_BlockedUserId" ON "UserBlocks" ("BlockedUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701085455_AddReportsAndBlocks') THEN
    CREATE UNIQUE INDEX "IX_UserBlocks_BlockerId_BlockedUserId" ON "UserBlocks" ("BlockerId", "BlockedUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701085455_AddReportsAndBlocks') THEN
    CREATE INDEX "IX_UserReports_ReporterId" ON "UserReports" ("ReporterId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701085455_AddReportsAndBlocks') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260701085455_AddReportsAndBlocks', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    CREATE TABLE "SupportTickets" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "Category" text NOT NULL,
        "Subject" text NOT NULL,
        "Status" text NOT NULL,
        "HasUnreadAdminReply" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        "ClosedAt" timestamp with time zone,
        CONSTRAINT "PK_SupportTickets" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SupportTickets_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    CREATE TABLE "SupportMessages" (
        "Id" uuid NOT NULL,
        "TicketId" uuid NOT NULL,
        "SenderType" text NOT NULL,
        "Body" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_SupportMessages" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SupportMessages_SupportTickets_TicketId" FOREIGN KEY ("TicketId") REFERENCES "SupportTickets" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    CREATE TABLE "SupportAttachments" (
        "Id" uuid NOT NULL,
        "MessageId" uuid NOT NULL,
        "FileUrl" text NOT NULL,
        "FileType" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_SupportAttachments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SupportAttachments_SupportMessages_MessageId" FOREIGN KEY ("MessageId") REFERENCES "SupportMessages" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    CREATE INDEX "IX_SupportAttachments_MessageId" ON "SupportAttachments" ("MessageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    CREATE INDEX "IX_SupportMessages_TicketId" ON "SupportMessages" ("TicketId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    CREATE INDEX "IX_SupportTickets_Status" ON "SupportTickets" ("Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    CREATE INDEX "IX_SupportTickets_UserId" ON "SupportTickets" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260701100109_AddSupportTickets') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260701100109_AddSupportTickets', '9.0.17');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260702190404_AddPackingList') THEN
    CREATE TABLE "PackingListCategories" (
        "Id" uuid NOT NULL,
        "TripId" uuid NOT NULL,
        "Name" text NOT NULL,
        "SortOrder" integer NOT NULL,
        "CreatedByUserId" uuid NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_PackingListCategories" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PackingListCategories_Trips_TripId" FOREIGN KEY ("TripId") REFERENCES "Trips" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260702190404_AddPackingList') THEN
    CREATE TABLE "PackingListItems" (
        "Id" uuid NOT NULL,
        "CategoryId" uuid NOT NULL,
        "Text" text NOT NULL,
        "IsChecked" boolean NOT NULL,
        "SortOrder" integer NOT NULL,
        "CreatedByUserId" uuid NOT NULL,
        "CheckedByUserId" uuid,
        "CheckedAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_PackingListItems" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PackingListItems_PackingListCategories_CategoryId" FOREIGN KEY ("CategoryId") REFERENCES "PackingListCategories" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260702190404_AddPackingList') THEN
    CREATE INDEX "IX_PackingListCategories_TripId" ON "PackingListCategories" ("TripId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260702190404_AddPackingList') THEN
    CREATE INDEX "IX_PackingListItems_CategoryId" ON "PackingListItems" ("CategoryId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260702190404_AddPackingList') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260702190404_AddPackingList', '9.0.17');
    END IF;
END $EF$;
COMMIT;

