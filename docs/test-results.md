# Test results

The complete run of the automated tests on 2026-09-30 09:54 UTC, written by `scripts\summarize-tests.ps1`.
The agents were the scripted stand-in: no test sends a request to a model. The tests that talk to the
installed agents are not part of this run; `docs\verification.md` says what they found.

| | |
|---|---:|
| Tests | 2300 |
| Passed | 2300 |
| Failed | 0 |
| Not run | 0 |
| Time | 9.4 minutes |

| Class | Passed | Failed |
|---|---:|---:|
| Adapters.ClaudeApprovalTests | 13 | 0 |
| Adapters.ClaudeCredentialTests | 5 | 0 |
| Adapters.ClaudeDetectionTests | 18 | 0 |
| Adapters.ClaudeSessionTests | 57 | 0 |
| Adapters.ClaudeTurnTests | 24 | 0 |
| Adapters.CodexApprovalAndControlTests | 39 | 0 |
| Adapters.CodexClosedInputTests | 3 | 0 |
| Adapters.CodexConnectionTests | 6 | 0 |
| Adapters.CodexDetectionTests | 15 | 0 |
| Adapters.CodexExecAdapterTests | 15 | 0 |
| Adapters.CodexRobustnessTests | 12 | 0 |
| Adapters.CodexSessionTests | 44 | 0 |
| Adapters.CodexTurnTests | 36 | 0 |
| Adapters.CodexWarningTests | 7 | 0 |
| Adapters.JsonLineReaderTests | 11 | 0 |
| Bench.BenchStatisticsTests | 18 | 0 |
| Bench.BenchToolTests | 42 | 0 |
| Console.ApprovalAnswerTests | 35 | 0 |
| Console.ApprovalDescriptionTests | 14 | 0 |
| Console.ArgumentSplittingTests | 7 | 0 |
| Console.CliCommandTests | 31 | 0 |
| Console.CommandCatalogTests | 31 | 0 |
| Console.CommandLineTests | 27 | 0 |
| Console.InputClassifierTests | 21 | 0 |
| Console.InputCompletionTests | 25 | 0 |
| Console.JsonOutputTests | 36 | 0 |
| Console.LiveInputTests | 22 | 0 |
| Console.PasteDetectorTests | 5 | 0 |
| Console.PlainApprovalTests | 11 | 0 |
| Console.RecordedRequestTests | 1 | 0 |
| Console.RunEventFormatterTests | 51 | 0 |
| Console.ScreenTests | 246 | 0 |
| Console.SettingsStoreTests | 7 | 0 |
| Console.ShellLifetimeTests | 4 | 0 |
| Console.ShellModelCommandTests | 59 | 0 |
| Console.ShellRecordCommandTests | 65 | 0 |
| Console.ShellRunCommandTests | 61 | 0 |
| Console.ShellTests | 37 | 0 |
| Coordinator.AdaptiveModeTests | 5 | 0 |
| Coordinator.ApprovalTests | 6 | 0 |
| Coordinator.ChecksAndSourceTests | 14 | 0 |
| Coordinator.ClaudeAsAModelTests | 26 | 0 |
| Coordinator.CodexAccountTests | 1 | 0 |
| Coordinator.CodexApprovalsReviewerTests | 2 | 0 |
| Coordinator.CodexCrashRecoveryTests | 3 | 0 |
| Coordinator.CrashRecoveryTests | 8 | 0 |
| Coordinator.DeliveryTests | 15 | 0 |
| Coordinator.FollowUpTests | 7 | 0 |
| Coordinator.LongPathTests | 2 | 0 |
| Coordinator.MechanicalEditTests | 8 | 0 |
| Coordinator.ProfileEnforcementTests | 10 | 0 |
| Coordinator.ProtectedChangeTests | 3 | 0 |
| Coordinator.RepairLoopTests | 16 | 0 |
| Coordinator.ReviewerBoundaryTests | 4 | 0 |
| Coordinator.RunPipelineTests | 13 | 0 |
| Coordinator.SafeguardTests | 18 | 0 |
| Coordinator.SessionSlotTests | 4 | 0 |
| Coordinator.SettingsVerifierTests | 82 | 0 |
| Coordinator.StartingReferenceTests | 4 | 0 |
| Coordinator.SteeringTests | 10 | 0 |
| Coordinator.StopAndLimitTests | 15 | 0 |
| Coordinator.TwoProviderTests | 3 | 0 |
| Coordinator.UsageRecordingTests | 5 | 0 |
| Coordinator.WorkspaceChoiceTests | 5 | 0 |
| Coordinator.WorkspacePathsTests | 19 | 0 |
| Core.AcceptanceGateTests | 36 | 0 |
| Core.AdaptivePolicyTests | 22 | 0 |
| Core.ProfileResolverTests | 33 | 0 |
| Core.PromptBuilderTests | 19 | 0 |
| Core.ReviewOutputParserTests | 22 | 0 |
| Core.RunStateMachineTests | 21 | 0 |
| Core.RuntimeTemplateTests | 4 | 0 |
| Core.SessionUsageTrackerTests | 6 | 0 |
| Core.TerminalSanitizerTests | 59 | 0 |
| Core.TimingTests | 7 | 0 |
| Core.TokenCountsTests | 4 | 0 |
| EndToEnd.ConsoleKeyTests | 5 | 0 |
| EndToEnd.ConsoleMeasurements | 2 | 0 |
| EndToEnd.ExecutableTests | 23 | 0 |
| EndToEnd.InteractiveConsoleTests | 24 | 0 |
| EndToEnd.ShellDriverTests | 17 | 0 |
| Packaging.ExamplesTests | 7 | 0 |
| Packaging.InstallerScriptTests | 52 | 0 |
| Packaging.LiveRunPartTests | 4 | 0 |
| Packaging.LiveRunRouteTests | 25 | 0 |
| Packaging.LiveRunRulesTests | 20 | 0 |
| Packaging.LiveRunScriptTests | 102 | 0 |
| Packaging.VersionTests | 3 | 0 |
| Platform.CommandLineTests | 18 | 0 |
| Platform.CredentialStoreTests | 13 | 0 |
| Platform.ExecutableResolverTests | 17 | 0 |
| Platform.PackagedProgramTests | 10 | 0 |
| Platform.ProcessRunnerTests | 17 | 0 |
| Storage.DatabaseLifecycleTests | 3 | 0 |
| Storage.JournalStoreTests | 5 | 0 |
| Storage.RunStoreTests | 18 | 0 |
| Storage.TrustStoreTests | 6 | 0 |
| Support.TempDirectoryTests | 2 | 0 |
| Validation.ConfigurationParsingTests | 19 | 0 |
| Validation.ConfigurationTrustTests | 7 | 0 |
| Validation.EnvironmentFingerprintTests | 4 | 0 |
| Validation.GateDetectionTests | 8 | 0 |
| Validation.GateRunTests | 19 | 0 |
| Workspace.ApplyTests | 13 | 0 |
| Workspace.BlobStoreTests | 8 | 0 |
| Workspace.DiffTests | 5 | 0 |
| Workspace.DiscardAndGuardTests | 4 | 0 |
| Workspace.ExecutionCopyTests | 5 | 0 |
| Workspace.FreezeTests | 12 | 0 |
| Workspace.GlobTests | 22 | 0 |
| Workspace.InspectionTests | 8 | 0 |
| Workspace.ManifestTests | 10 | 0 |
| Workspace.MechanicalEditTests | 10 | 0 |
| Workspace.MergeTests | 4 | 0 |
| Workspace.PrepareTests | 11 | 0 |
| Workspace.RestoreCandidateTests | 4 | 0 |
| Workspace.SecretPathTests | 14 | 0 |
| Workspace.UndoTests | 7 | 0 |
| Workspace.UnifiedDiffTests | 11 | 0 |
