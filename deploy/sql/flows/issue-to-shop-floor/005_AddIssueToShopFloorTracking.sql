-- Schema script for Issue to Shop Floor process tracking tables:
-- IssueToShopFloorConversion and IssueToShopFloorOutcome

IF OBJECT_ID(N'[IssueToShopFloorConversion]', N'U') IS NULL
BEGIN
    CREATE TABLE [IssueToShopFloorConversion] (
        [Id] uniqueidentifier NOT NULL,
        [IssueSource] nvarchar(20) NOT NULL,
        [DocumentNumber] nvarchar(50) NOT NULL,
        [DocumentId] bigint NOT NULL,
        [SiteId] int NOT NULL,
        [SiteCode] nvarchar(20) NOT NULL,
        [FirstSeenAtUtc] datetimeoffset NOT NULL,
        [LastAttemptedAtUtc] datetimeoffset NOT NULL,
        [TotalAttempts] int NOT NULL,
        [SuccessfulAttempts] int NOT NULL,
        [IsTerminal] bit NOT NULL,
        [TerminalIssueNumber] nvarchar(50) NULL,
        CONSTRAINT [PK_IssueToShopFloorConversion] PRIMARY KEY ([Id])
    );

    CREATE UNIQUE INDEX [UX_IssueToShopFloorConversion_Document] ON [IssueToShopFloorConversion] ([IssueSource], [DocumentNumber]);
    CREATE INDEX [IX_IssueToShopFloorConversion_DocNo] ON [IssueToShopFloorConversion] ([DocumentNumber]);
    CREATE INDEX [IX_IssueToShopFloorConversion_SiteId] ON [IssueToShopFloorConversion] ([SiteId]);
END;

IF OBJECT_ID(N'[IssueToShopFloorOutcome]', N'U') IS NULL
BEGIN
    CREATE TABLE [IssueToShopFloorOutcome] (
        [Id] uniqueidentifier NOT NULL,
        [JobId] uniqueidentifier NOT NULL,
        [IssueSource] nvarchar(20) NOT NULL,
        [DocumentNumber] nvarchar(50) NOT NULL,
        [Outcome] nvarchar(20) NOT NULL,
        [IssueNumber] nvarchar(50) NULL,
        [LineCount] int NOT NULL,
        [TotalQuantity] decimal(18, 4) NOT NULL,
        [RefusalReason] nvarchar(1000) NULL,
        [ChecksJson] nvarchar(max) NULL,
        [ShortagesJson] nvarchar(max) NULL,
        [LinesJson] nvarchar(max) NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_IssueToShopFloorOutcome] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_IssueToShopFloorOutcome_AutomationJob_JobId] FOREIGN KEY ([JobId]) REFERENCES [AutomationJob] ([Id]) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX [UX_IssueToShopFloorOutcome_JobId] ON [IssueToShopFloorOutcome] ([JobId]);
    CREATE INDEX [IX_IssueToShopFloorOutcome_DocNo] ON [IssueToShopFloorOutcome] ([DocumentNumber]);
    CREATE INDEX [IX_IssueToShopFloorOutcome_IssueNo] ON [IssueToShopFloorOutcome] ([IssueNumber]);
END;
