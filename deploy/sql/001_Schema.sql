IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE TABLE [AutomationConfig] (
        [Id] uniqueidentifier NOT NULL,
        [Module] nvarchar(50) NOT NULL,
        [EnableAgent] bit NOT NULL,
        [EnableModule] bit NOT NULL,
        [Mode] nvarchar(10) NOT NULL,
        [PollIntervalSeconds] int NOT NULL,
        [ReconcileIntervalMinutes] int NOT NULL,
        [WorkingHoursStart] time NULL,
        [WorkingHoursEnd] time NULL,
        [RetryCount] int NOT NULL,
        [ParallelWorkers] int NOT NULL,
        [LoggingLevel] nvarchar(20) NOT NULL,
        [IsLicensed] bit NOT NULL,
        [PayloadRetentionDays] int NOT NULL,
        [LogRetentionDays] int NOT NULL,
        [ErrorRetentionDays] int NOT NULL,
        [UpdatedAtUtc] datetimeoffset NOT NULL,
        [UpdatedBy] nvarchar(100) NULL,
        CONSTRAINT [PK_AutomationConfig] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE TABLE [AutomationError] (
        [Id] uniqueidentifier NOT NULL,
        [JobId] uniqueidentifier NOT NULL,
        [StepId] uniqueidentifier NULL,
        [ErrorType] nvarchar(20) NOT NULL,
        [TechnicalMessage] nvarchar(max) NOT NULL,
        [LaymanMessage] nvarchar(1000) NOT NULL,
        [StackTrace] nvarchar(max) NULL,
        [ApiEndpoint] nvarchar(500) NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_AutomationError] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE TABLE [AutomationJob] (
        [Id] uniqueidentifier NOT NULL,
        [CorrelationId] nvarchar(64) NOT NULL,
        [WorkflowType] nvarchar(50) NOT NULL,
        [DocumentType] nvarchar(50) NOT NULL,
        [DocumentId] nvarchar(100) NOT NULL,
        [Mode] nvarchar(10) NOT NULL,
        [Priority] int NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CurrentStage] nvarchar(50) NULL,
        [RetryCount] int NOT NULL,
        [IdempotencyKey] nchar(64) NOT NULL,
        [ApprovedBy] nvarchar(100) NULL,
        [ApprovedAtUtc] datetimeoffset NULL,
        [CancelledBy] nvarchar(100) NULL,
        [CancellationReason] nvarchar(1000) NULL,
        [NotBeforeUtc] datetimeoffset NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [StartedAtUtc] datetimeoffset NULL,
        [CompletedAtUtc] datetimeoffset NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_AutomationJob] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE TABLE [AutomationLog] (
        [Id] uniqueidentifier NOT NULL,
        [JobId] uniqueidentifier NOT NULL,
        [StepId] uniqueidentifier NULL,
        [CorrelationId] nvarchar(64) NOT NULL,
        [Module] nvarchar(50) NULL,
        [ApiEndpoint] nvarchar(500) NULL,
        [StartedAtUtc] datetimeoffset NOT NULL,
        [CompletedAtUtc] datetimeoffset NOT NULL,
        [DurationMs] bigint NOT NULL,
        [Result] nvarchar(50) NOT NULL,
        CONSTRAINT [PK_AutomationLog] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE TABLE [AutomationJobStep] (
        [Id] uniqueidentifier NOT NULL,
        [JobId] uniqueidentifier NOT NULL,
        [Stage] nvarchar(50) NOT NULL,
        [OperationName] nvarchar(100) NOT NULL,
        [Sequence] int NOT NULL,
        [Kind] nvarchar(20) NOT NULL,
        [Target] nvarchar(50) NULL,
        [Status] nvarchar(20) NOT NULL,
        [RetryCount] int NOT NULL,
        [RequestPayload] nvarchar(max) NULL,
        [ResponsePayload] nvarchar(max) NULL,
        [ErpDocumentRef] nvarchar(100) NULL,
        [Remarks] nvarchar(1000) NULL,
        [ApprovedBy] nvarchar(100) NULL,
        [ApprovedAtUtc] datetimeoffset NULL,
        [StartedAtUtc] datetimeoffset NULL,
        [CompletedAtUtc] datetimeoffset NULL,
        CONSTRAINT [PK_AutomationJobStep] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AutomationJobStep_AutomationJob_JobId] FOREIGN KEY ([JobId]) REFERENCES [AutomationJob] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE UNIQUE INDEX [UX_AutomationConfig_Module] ON [AutomationConfig] ([Module]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationError_CreatedAtUtc] ON [AutomationError] ([CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationError_JobId] ON [AutomationError] ([JobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationJob_Claim] ON [AutomationJob] ([Status], [Priority], [CreatedAtUtc]) INCLUDE ([NotBeforeUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationJob_CreatedAtUtc] ON [AutomationJob] ([CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationJob_DocumentId] ON [AutomationJob] ([DocumentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_AutomationJob_IdempotencyKey_Live] ON [AutomationJob] ([IdempotencyKey]) WHERE [Status] <> ''Cancelled''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationJobStep_Job_Status] ON [AutomationJobStep] ([JobId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE UNIQUE INDEX [UX_AutomationJobStep_Job_Sequence] ON [AutomationJobStep] ([JobId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationLog_CorrelationId] ON [AutomationLog] ([CorrelationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationLog_JobId] ON [AutomationLog] ([JobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    CREATE INDEX [IX_AutomationLog_StartedAtUtc] ON [AutomationLog] ([StartedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260727164450_InitialAutomationSchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260727164450_InitialAutomationSchema', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729085342_AddLiveCycleIndex'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_AutomationJob_LiveCycle] ON [AutomationJob] ([WorkflowType]) WHERE [DocumentType] = ''Cycle'' AND [Status] <> ''Completed'' AND [Status] <> ''Cancelled''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729085342_AddLiveCycleIndex'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260729085342_AddLiveCycleIndex', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    DROP INDEX [UX_AutomationJob_IdempotencyKey_Live] ON [AutomationJob];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    ALTER TABLE [AutomationJob] ADD [ConversionId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    ALTER TABLE [AutomationJob] ADD [RunId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    EXEC(N'ALTER TABLE [AutomationJob] ADD [DurationMs] AS DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE TABLE [AutomationRun] (
        [Id] uniqueidentifier NOT NULL,
        [CorrelationId] nvarchar(64) NOT NULL,
        [WorkflowType] nvarchar(50) NOT NULL,
        [TriggerSource] nvarchar(20) NOT NULL,
        [TriggeredBy] nvarchar(100) NULL,
        [TriggerReference] nvarchar(200) NULL,
        [Mode] nvarchar(10) NOT NULL,
        [RequestedIndentTypes] nvarchar(60) NOT NULL,
        [RequestedSites] nvarchar(200) NULL,
        [MaxIndents] int NULL,
        [Status] nvarchar(20) NOT NULL,
        [IndentsExamined] int NOT NULL,
        [JobsCreated] int NOT NULL,
        [PurchaseOrdersCreated] int NOT NULL,
        [FailureReason] nvarchar(1000) NULL,
        [StartedAtUtc] datetimeoffset NOT NULL,
        [CompletedAtUtc] datetimeoffset NULL,
        [DurationMs] AS DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc]),
        CONSTRAINT [PK_AutomationRun] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE TABLE [IndentPoConversion] (
        [Id] uniqueidentifier NOT NULL,
        [IndentId] bigint NOT NULL,
        [IndentKind] nvarchar(10) NOT NULL,
        [IndentNumber] nvarchar(50) NOT NULL,
        [SiteId] int NOT NULL,
        [IndentDate] date NULL,
        [FirstSeenAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_IndentPoConversion] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE TABLE [IndentPoOutcome] (
        [Id] uniqueidentifier NOT NULL,
        [JobId] uniqueidentifier NOT NULL,
        [StepId] uniqueidentifier NULL,
        [Sequence] int NOT NULL,
        [Outcome] nvarchar(20) NOT NULL,
        [VendorCode] nvarchar(20) NULL,
        [CurrencyCode] nvarchar(5) NULL,
        [RateStructureCode] nvarchar(20) NULL,
        [PoId] bigint NULL,
        [PoNumber] nvarchar(50) NULL,
        [LineCount] int NOT NULL,
        [Reason] nvarchar(1000) NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_IndentPoOutcome] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_IndentPoOutcome_Result] CHECK (([Outcome] = 'Created' AND [PoId] IS NOT NULL AND [PoNumber] IS NOT NULL) OR ([Outcome] <> 'Created' AND [Reason] IS NOT NULL)),
        CONSTRAINT [FK_IndentPoOutcome_AutomationJobStep_StepId] FOREIGN KEY ([StepId]) REFERENCES [AutomationJobStep] ([Id]),
        CONSTRAINT [FK_IndentPoOutcome_AutomationJob_JobId] FOREIGN KEY ([JobId]) REFERENCES [AutomationJob] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_AutomationJob_Conversion] ON [AutomationJob] ([ConversionId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_AutomationJob_RunId] ON [AutomationJob] ([RunId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_AutomationJob_IdempotencyKey_Live] ON [AutomationJob] ([IdempotencyKey]) WHERE [Status] <> ''Cancelled'' AND [ConversionId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_AutomationJob_LiveIndentConversion] ON [AutomationJob] ([IdempotencyKey]) WHERE [ConversionId] IS NOT NULL AND [Status] <> ''Completed'' AND [Status] <> ''Cancelled'' AND [Status] <> ''Failed''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_AutomationRun_CorrelationId] ON [AutomationRun] ([CorrelationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_AutomationRun_StartedAtUtc] ON [AutomationRun] ([StartedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_AutomationRun_Trigger] ON [AutomationRun] ([TriggerSource], [StartedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_IndentPoConversion_IndentNumber] ON [IndentPoConversion] ([IndentNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_IndentPoConversion_SiteId] ON [IndentPoConversion] ([SiteId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE UNIQUE INDEX [UX_IndentPoConversion_Indent] ON [IndentPoConversion] ([IndentKind], [IndentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_IndentPoOutcome_StepId] ON [IndentPoOutcome] ([StepId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE INDEX [IX_IndentPoOutcome_VendorCode] ON [IndentPoOutcome] ([VendorCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    CREATE UNIQUE INDEX [UX_IndentPoOutcome_Job_Sequence] ON [IndentPoOutcome] ([JobId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_IndentPoOutcome_PoId] ON [IndentPoOutcome] ([PoId]) WHERE [PoId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    ALTER TABLE [AutomationJob] ADD CONSTRAINT [FK_AutomationJob_AutomationRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [AutomationRun] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    ALTER TABLE [AutomationJob] ADD CONSTRAINT [FK_AutomationJob_IndentPoConversion_ConversionId] FOREIGN KEY ([ConversionId]) REFERENCES [IndentPoConversion] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260831073910_AddIndentToPoTracking'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260831073910_AddIndentToPoTracking', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903070956_AddIndentPoAutomationConfig'
)
BEGIN
    CREATE TABLE [IndentPoAutomationConfig] (
        [Id] uniqueidentifier NOT NULL,
        [IndentKind] nvarchar(20) NOT NULL,
        [RunMode] nvarchar(20) NOT NULL,
        [ScheduleTime] time NULL,
        [Sites] nvarchar(200) NULL,
        [IsActive] bit NOT NULL,
        [DryRun] bit NOT NULL,
        [MaxIndentsPerRun] int NULL,
        [LastScheduledRunDate] date NULL,
        [LastTriggeredAtUtc] datetimeoffset NULL,
        [LastRunStatus] nvarchar(20) NULL,
        [LastRunReference] uniqueidentifier NULL,
        [UpdatedAtUtc] datetimeoffset NOT NULL,
        [UpdatedBy] nvarchar(100) NULL,
        CONSTRAINT [PK_IndentPoAutomationConfig] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903070956_AddIndentPoAutomationConfig'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'DryRun', N'IndentKind', N'IsActive', N'LastRunReference', N'LastRunStatus', N'LastScheduledRunDate', N'LastTriggeredAtUtc', N'MaxIndentsPerRun', N'RunMode', N'ScheduleTime', N'Sites', N'UpdatedAtUtc', N'UpdatedBy') AND [object_id] = OBJECT_ID(N'[IndentPoAutomationConfig]'))
        SET IDENTITY_INSERT [IndentPoAutomationConfig] ON;
    EXEC(N'INSERT INTO [IndentPoAutomationConfig] ([Id], [DryRun], [IndentKind], [IsActive], [LastRunReference], [LastRunStatus], [LastScheduledRunDate], [LastTriggeredAtUtc], [MaxIndentsPerRun], [RunMode], [ScheduleTime], [Sites], [UpdatedAtUtc], [UpdatedBy])
    VALUES (''6f1d4c20-0000-0000-0000-00000000000a'', CAST(0 AS bit), N''Regular'', CAST(0 AS bit), NULL, NULL, NULL, NULL, NULL, N''Disabled'', NULL, NULL, ''2026-01-01T00:00:00.0000000+00:00'', NULL),
    (''6f1d4c20-0000-0000-0000-00000000000b'', CAST(0 AS bit), N''Capital'', CAST(0 AS bit), NULL, NULL, NULL, NULL, NULL, N''Disabled'', NULL, NULL, ''2026-01-01T00:00:00.0000000+00:00'', NULL),
    (''6f1d4c20-0000-0000-0000-00000000000c'', CAST(0 AS bit), N''Service'', CAST(0 AS bit), NULL, NULL, NULL, NULL, NULL, N''Disabled'', NULL, NULL, ''2026-01-01T00:00:00.0000000+00:00'', NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'DryRun', N'IndentKind', N'IsActive', N'LastRunReference', N'LastRunStatus', N'LastScheduledRunDate', N'LastTriggeredAtUtc', N'MaxIndentsPerRun', N'RunMode', N'ScheduleTime', N'Sites', N'UpdatedAtUtc', N'UpdatedBy') AND [object_id] = OBJECT_ID(N'[IndentPoAutomationConfig]'))
        SET IDENTITY_INSERT [IndentPoAutomationConfig] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903070956_AddIndentPoAutomationConfig'
)
BEGIN
    CREATE UNIQUE INDEX [UX_IndentPoAutomationConfig_IndentKind] ON [IndentPoAutomationConfig] ([IndentKind]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903070956_AddIndentPoAutomationConfig'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260903070956_AddIndentPoAutomationConfig', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903195734_AddIndentNumbersToPoAutomationConfig'
)
BEGIN
    ALTER TABLE [IndentPoAutomationConfig] ADD [IndentNumbers] nvarchar(4000) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903195734_AddIndentNumbersToPoAutomationConfig'
)
BEGIN
    EXEC(N'UPDATE [IndentPoAutomationConfig] SET [IndentNumbers] = NULL
    WHERE [Id] = ''6f1d4c20-0000-0000-0000-00000000000a'';
    SELECT @@ROWCOUNT');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903195734_AddIndentNumbersToPoAutomationConfig'
)
BEGIN
    EXEC(N'UPDATE [IndentPoAutomationConfig] SET [IndentNumbers] = NULL
    WHERE [Id] = ''6f1d4c20-0000-0000-0000-00000000000b'';
    SELECT @@ROWCOUNT');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903195734_AddIndentNumbersToPoAutomationConfig'
)
BEGIN
    EXEC(N'UPDATE [IndentPoAutomationConfig] SET [IndentNumbers] = NULL
    WHERE [Id] = ''6f1d4c20-0000-0000-0000-00000000000c'';
    SELECT @@ROWCOUNT');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903195734_AddIndentNumbersToPoAutomationConfig'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260903195734_AddIndentNumbersToPoAutomationConfig', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260907084918_PoAutomationModeDefault'
)
BEGIN
    EXEC(N'UPDATE [IndentPoAutomationConfig] SET [RunMode] = N''Api''
    WHERE [Id] = ''6f1d4c20-0000-0000-0000-00000000000a'';
    SELECT @@ROWCOUNT');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260907084918_PoAutomationModeDefault'
)
BEGIN
    EXEC(N'UPDATE [IndentPoAutomationConfig] SET [RunMode] = N''Api''
    WHERE [Id] = ''6f1d4c20-0000-0000-0000-00000000000b'';
    SELECT @@ROWCOUNT');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260907084918_PoAutomationModeDefault'
)
BEGIN
    EXEC(N'UPDATE [IndentPoAutomationConfig] SET [RunMode] = N''Api''
    WHERE [Id] = ''6f1d4c20-0000-0000-0000-00000000000c'';
    SELECT @@ROWCOUNT');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260907084918_PoAutomationModeDefault'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260907084918_PoAutomationModeDefault', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916065009_AddSkippedJobStatus'
)
BEGIN
    DROP INDEX [UX_AutomationJob_LiveIndentConversion] ON [AutomationJob];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916065009_AddSkippedJobStatus'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_AutomationJob_LiveIndentConversion] ON [AutomationJob] ([IdempotencyKey]) WHERE [ConversionId] IS NOT NULL AND [Status] <> ''Completed'' AND [Status] <> ''Cancelled'' AND [Status] <> ''Failed'' AND [Status] <> ''Skipped''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916065009_AddSkippedJobStatus'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916065009_AddSkippedJobStatus', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    CREATE TABLE [PoGrnAutomationConfig] (
        [Id] uniqueidentifier NOT NULL,
        [IsActive] bit NOT NULL,
        [RunMode] nvarchar(20) NOT NULL,
        [ScheduleTime] time NULL,
        [ReceiptMode] nvarchar(20) NOT NULL,
        [InvoiceNumber] nvarchar(50) NULL,
        [Sites] nvarchar(200) NULL,
        [DryRun] bit NOT NULL,
        [MaxPosPerRun] int NULL,
        [LastScheduledRunDate] date NULL,
        [LastTriggeredAtUtc] datetimeoffset NULL,
        [LastRunStatus] nvarchar(20) NULL,
        [LastRunReference] uniqueidentifier NULL,
        [UpdatedAtUtc] datetimeoffset NOT NULL,
        [UpdatedBy] nvarchar(100) NULL,
        CONSTRAINT [PK_PoGrnAutomationConfig] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    CREATE TABLE [PoGrnRun] (
        [Id] uniqueidentifier NOT NULL,
        [Trigger] nvarchar(20) NOT NULL,
        [TriggeredBy] nvarchar(100) NULL,
        [TriggerReference] nvarchar(100) NULL,
        [ReceiptMode] nvarchar(20) NOT NULL,
        [RequestedSites] nvarchar(200) NULL,
        [Status] nvarchar(20) NOT NULL,
        [StartedAtUtc] datetimeoffset NOT NULL,
        [CompletedAtUtc] datetimeoffset NULL,
        [PosExamined] int NOT NULL,
        [GrnsCreated] int NOT NULL,
        [PosSkipped] int NOT NULL,
        [PosFailed] int NOT NULL,
        [FailureReason] nvarchar(2000) NULL,
        CONSTRAINT [PK_PoGrnRun] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    CREATE TABLE [PoGrnReceipt] (
        [Id] uniqueidentifier NOT NULL,
        [RunId] uniqueidentifier NOT NULL,
        [PoId] bigint NOT NULL,
        [PoNumber] nvarchar(60) NOT NULL,
        [PoType] nvarchar(5) NOT NULL,
        [SiteId] int NOT NULL,
        [VendorCode] nvarchar(20) NOT NULL,
        [WarehouseId] int NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [GrnId] bigint NULL,
        [GrnNumber] nvarchar(60) NULL,
        [LinesReceived] int NOT NULL,
        [LinesSkipped] int NOT NULL,
        [Reason] nvarchar(4000) NULL,
        [RecordedAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_PoGrnReceipt] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_PoGrnReceipt_Result] CHECK (([Status] = 'Created' AND [GrnNumber] IS NOT NULL) OR ([Status] <> 'Created' AND [Reason] IS NOT NULL)),
        CONSTRAINT [FK_PoGrnReceipt_PoGrnRun_RunId] FOREIGN KEY ([RunId]) REFERENCES [PoGrnRun] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'DryRun', N'InvoiceNumber', N'IsActive', N'LastRunReference', N'LastRunStatus', N'LastScheduledRunDate', N'LastTriggeredAtUtc', N'MaxPosPerRun', N'ReceiptMode', N'RunMode', N'ScheduleTime', N'Sites', N'UpdatedAtUtc', N'UpdatedBy') AND [object_id] = OBJECT_ID(N'[PoGrnAutomationConfig]'))
        SET IDENTITY_INSERT [PoGrnAutomationConfig] ON;
    EXEC(N'INSERT INTO [PoGrnAutomationConfig] ([Id], [DryRun], [InvoiceNumber], [IsActive], [LastRunReference], [LastRunStatus], [LastScheduledRunDate], [LastTriggeredAtUtc], [MaxPosPerRun], [ReceiptMode], [RunMode], [ScheduleTime], [Sites], [UpdatedAtUtc], [UpdatedBy])
    VALUES (''7a2e5d30-0000-0000-0000-000000000001'', CAST(0 AS bit), NULL, CAST(0 AS bit), NULL, NULL, NULL, NULL, NULL, N''Complete'', N''Api'', NULL, NULL, ''2026-09-24T00:00:00.0000000+00:00'', NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'DryRun', N'InvoiceNumber', N'IsActive', N'LastRunReference', N'LastRunStatus', N'LastScheduledRunDate', N'LastTriggeredAtUtc', N'MaxPosPerRun', N'ReceiptMode', N'RunMode', N'ScheduleTime', N'Sites', N'UpdatedAtUtc', N'UpdatedBy') AND [object_id] = OBJECT_ID(N'[PoGrnAutomationConfig]'))
        SET IDENTITY_INSERT [PoGrnAutomationConfig] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    CREATE INDEX [IX_PoGrnReceipt_PoId_RecordedAtUtc] ON [PoGrnReceipt] ([PoId], [RecordedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    CREATE INDEX [IX_PoGrnReceipt_RunId] ON [PoGrnReceipt] ([RunId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    CREATE INDEX [IX_PoGrnRun_StartedAtUtc] ON [PoGrnRun] ([StartedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924103545_AddPoToGrn'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260924103545_AddPoToGrn', N'10.0.10');
END;

COMMIT;
GO

