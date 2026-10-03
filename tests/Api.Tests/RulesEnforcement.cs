namespace Vuelto.Api.Tests;

/// <summary>
/// The enforcement manifest for FOUNDATION_RULES v3.0 (v4 audit T67): every rule the rules file marks
/// <c>[machine]</c> names here the standing check that enforces it — a test method, a test class, or a CI step
/// (<c>ci:</c> prefix) — and <see cref="EnforcementGateTests.EveryMachineRule_NamesAStandingCheck"/> verifies that
/// each named check exists in the tree and that no machine rule is missing from this list. A rule whose check is
/// still to be built says so with the tracker issue that owns it, so "machine-enforced on paper" is at least
/// visible; the audit found v3 fixes marked done that never landed, and this is what keeps that from recurring.
/// <para>
/// The entry for a rule IS its test-name manifest where the rule asks for one (R84: the session-continuity
/// set in <c>SessionKeepAliveTests</c>). Rename a test and this file follows in the same commit, or the gate fails.
/// </para>
/// </summary>
public static class RulesEnforcement
{
    /// <param name="Rule">The final rule id.</param>
    /// <param name="Checks">Test method names, test class names, or <c>ci:&lt;step name&gt;</c> (present in both workflow copies).</param>
    /// <param name="NotHere">When the thing the rule guards does not exist in this repo: where it lives instead.</param>
    /// <param name="Pending">When part of the mechanism is still unbuilt: the owning tracker issue and what is missing.</param>
    public sealed record Entry(string Rule, string[] Checks, string? Pending = null, string? NotHere = null);

    public static readonly Entry[] Manifest =
    [
        // ── 2. Auth / session ──
        new("R81", ["Refresh_ThirdPresentationInsideGrace_Is401_AndRevokesAllSessions", "TryConsumeGrace_IsOneShot_AndStampsTheClock", "Inspect_RotatedTokenWithinGrace_AfterTheGraceWasSpent_IsReuse"]),
        new("R82", ["SignupSettingsTests"]),
        new("R84", ["UnreachableThroughExpiry_ThenRejected_RaisesSignedOutExactlyOnce", "MidSessionRejected_NavigatesToLogin_FromAProtectedRoute",
                    "DeviceClockSlow_ARequestAfterRealExpiry_RenewsBeforeSending", "DeviceClockFast_NeverLoopsRotations_AndReportsSignedIn",
                    "MidSession_RepeatedFailures_BackOff_UpToACap", "MidSession_ServerError_KeepsTheSession_AndTriesAgainShortly",
                    "Startup_RetriesWhileTheServerWakesUp_ThenSignsIn", "Startup_ServerStillDown_ShowsSignedOut_ButKeepsTheToken",
                    "Resume_RenewsASessionWhoseRenewalFailedWhileAway"]),
        new("R107", ["ClientRefreshTimeoutPlusRetry_FitsInsideTheServersReuseGrace", "AHungRefresh_TimesOut_WithinTheGraceWindow"]),
        new("R108", ["Native_RejectionNeedsTheApisOwnErrorBody", "Native_ServerError_KeepsTheStoredRefreshToken", "Native_Unreachable_KeepsTheStoredRefreshToken", "Native_Rejected_ClearsTheStoredRefreshToken"]),
        new("R111", ["MidSessionRejected_NavigatesToLogin_FromAProtectedRoute"]),
        new("R112", ["SignupRefusedCopyTests"]),
        new("R123", ["DeleteTenantAsync_IsCalledOnlyByTheDissolutionSequence", "AcceptDissolveTests"]),
        new("R124", ["Logout_WithTheTokenJustRotatedOut_StillRevokesTheSuccessor", "Logout_WithAnExpiredToken_RevokesTheUsersOtherSessions", "Logout_WithAnUnknownToken_IsANoOp_AndStill200"]),
        new("R125", ["ARefreshInFlight_WhenImpersonationBegins_IsDiscarded", "Impersonation_IsEnteredAndLeftExplicitly_AndStopRestoresTheStaffIdentity", "ImpersonationExpiry_DoesNotSilentlyRenewIntoTheStaffIdentity"]),
        new("R126", ["DeviceClockSlow_ARequestAfterRealExpiry_RenewsBeforeSending", "ABearerRequest_Refused401_RefreshesAndRetriesOnce"]),
        new("R127", ["InvitationExpiry_IsDecidedOnlyByTheEntityPredicate", "ExpiredPendingInvites_DoNotReserveSeats", "Invitation_ExpiringNow_IsInvalidForGateAndAccept"]),

        // ── 3. Tenancy / erasure completeness / RLS ──
        new("R90", ["OutboxRetentionTests"]),
        new("R91", ["OutboxTenancyTests"]),
        new("R129", ["EnterTenant_WithARequestSuppliedTenantId_ChecksTheTenantExists", "Webhook_ForUnknownTenant_IsIgnored_ClaimedButNothingWritten"]),
        new("R145", ["EveryTenantOwnedEntity_IsWiredIntoTenantDissolution", "OutboxTenancyTests"], Pending: "vuelto#95 (T27: the canary over nullable-TenantId tables)"),
        new("R146", ["EveryCrossTenantTest_SeedsARealSecondTenant", "TheScan_CatchesARandomIdArrange"]),
        new("R150", [], Pending: "vuelto#94 (T26: the migration Down-on-data walk)"),
        new("R151", [], Pending: "vuelto#93 (T25: recording doubles for the pre-auth cross-tenant reads)"),

        // ── 4. Admin / config gates / billing ──
        new("R86", ["GateOff_NothingUnderTheGatedPrefixes_IsMapped", "GateOff_AdminComp_Returns404_BeforeTheStaffCheck"]),
        new("R87", ["AGatedSwitch_IsReadOnlyThroughItsSettingsClass"]),
        new("R88", [], Pending: "vuelto#113 (T47: the stale-count doc-grep gate)"),
        new("R122", ["ConfigPostureTests"]),
        new("R128", ["StripeBillingProviderTests"]),
        new("R133", ["LapseSweep_NotificationAndStamp_AreOneTransaction", "JobTests_UseTheRealOutboxEmailSender"]),
        new("R134", ["PeriodKey_IsTheInvariantCalendar_WhateverTheRequestCultureIs", "ServerCode_FormatsYearsWithTheInvariantCulture"]),
        new("R153", ["EverySectionBoundSettingsClass_DeclaresItsSectionName", "ReadKeysIn_SeesEveryReadShape"]),
        new("R154", [], Pending: "vuelto#125 (T59: the per-gate harness seam and all-gates-on Postman parity)"),

        // ── 5. Jobs / email / webhooks / observability ──
        new("R89", ["Handler_RecordsAnEnumeratedReason_NeverTheExceptionText", "NoErrorColumn_IsAssignedFromAnExceptionMessage"]),
        new("R92", ["EmailAttachmentTests"]),
        new("R93", ["LogTemplates_NeverCarryAnEmailAddress"]),
        new("R95", ["ReadKeysIn_SeesEveryReadShape", "OtlpProtocolAndProbeTests"]),
        new("R96", ["ProcessDue_FailingHandler_WithAnEmojiAtTheTruncationBoundary_StillRecordsTheAttempt", "NoErrorColumn_IsAssignedFromAnExceptionMessage"]),
        new("R130", ["WebhookRedirectAndPinningTests"]),
        new("R131", ["ProcessDue_ClaimCountsTheAttempt_AndSchedulesTheRetry_BeforeTheHandlerRuns", "Processor_BookkeepingWriteFails_StillCountsTheAttempt", "ProcessDue_AttemptsAlreadyExhaustedAtClaim_DeadLettersWithoutRunningTheHandler"]),
        new("R132", ["EmailAttachmentTests"]),
        new("R135", ["Backoff_ClampsTheExponent_SoItNeverOverflows", "ProcessDue_FailingHandler_WithAnEmojiAtTheTruncationBoundary_StillRecordsTheAttempt"]),

        // ── 6. Deploy / CI / supply-chain ──
        new("R97", ["EveryRepoFileTheTestsRead_ClassifiesAsCode", "Classifier_CountsEveryFileAGateReads_AsCode"]),
        new("R98", ["EveryCheckout_LeavesNoTokenBehind"]),
        new("R99", ["EveryContainerImage_IsPinned_NotFloating"], Pending: "vuelto#88 (T16: widen to workflow services:/docker run and Dockerfile FROM)"),
        new("R136", ["CiShellLogic_PassesItsFixtures", "CiShellLogic_EveryAnchoredBlockIsATarget_WithCases"]),
        new("R137", ["ChangedFileLists_AreByteSafe"]),
        new("R139", ["NativeSmokeProviderProbe_MatchesTheStatusField_InBothSites"]),
        new("R140", ["PublishScript_ThrowsUnlessTheSignatureIsVerified_AndFindsTheSdkThroughAndroidHome"], Pending: "vuelto#118 (T52: the pwsh harness with fakes)"),
        new("R143", ["Classifier_FailsOpen_OnEveryOutput"]),

        // ── 7. Native ──
        new("R102", ["BearerScopedHandlerTests", "OnlyTheSharedRetry_BuildsABearerHeader"]),
        new("R103", ["ReleaseLeg_BuildsRelease_VerifiesV2OrV3_AndProvesTheGuardFires", "PublishScript_ThrowsUnlessTheSignatureIsVerified_AndFindsTheSdkThroughAndroidHome", "ci:apksigner verify — v2 or v3, the schemes Android 11+ installs"]),
        new("R104", [], Pending: "vuelto#117 (T51: signing-material gitignore + doc-grep gates)"),
        new("R105", [], Pending: "vuelto#117 (T51: NativeShellGateTests — allowBackup is still true)"),
        new("R106", ["NativeClassifier_CoversEveryNativeInput"]),
        new("R141", ["ReleaseGuards_RefuseBadInputs_AndPassGoodOnes", "ReleaseGuards_LiveInOneFile_UnderOneCondition"]),
        new("R142", [], Pending: "vuelto#119 (T53: node --test stubs for the wwwroot/js bootstraps)"),

        // ── 8. Client / test-completeness / harness ──
        new("R109", ["E2eNavigations_GoThroughBlazorBoot", "BootRetryBudget_IsOneNumber_InTheSuiteAndTheSlowestJourneysStep", "BlazorBootTests"]),
        new("R113", ["E2eShardsTests"]),
        new("R144", ["ClientRefreshTimeoutPlusRetry_FitsInsideTheServersReuseGrace", "PrTemplate_CarriesTheRuleCheckboxes"]),
        new("R147", [], Pending: "vuelto#114 (T48: the gate-off E2E lane in both copies)"),
        new("R148", [], Pending: "vuelto#123 (T57: the RCL clock gate)"),
        new("R149", [], Pending: "vuelto#125 (T59: the test-id contract)"),
        new("R155", ["RouteTableGuardTests"]),
        new("R157", ["SliceReferenceInspectorTests", "RoutePrefixInspectorTests"]),

        // ── 9. Docs / course / rule hygiene / template ──
        new("R83", ["PostmanParityTests"]),
        new("R114", [], NotHere: "the course (docs/tutorial) lives in perezosoft-platform; its gates run there"),
        new("R115", [], NotHere: "the course (docs/tutorial) lives in perezosoft-platform; its gates run there"),
        new("R116", ["RuleIds_CitedInTests_AreFinalRules", "EveryMachineRule_NamesAStandingCheck"]),
        new("R118", ["ClaudeMdDocMap_ListsEveryTopLevelDoc"], Pending: "vuelto#128 (T63: widen the doc-map gate to docs/**)"),
        new("R119", [], Pending: "vuelto#128 (T63: the Postman gate/refusal description floor)"),
        new("R120", [], Pending: "vuelto#128 (T63: the compiled-in limits block gate)"),
        new("R121", [], Pending: "vuelto#128 (T63: diagram currency)"),
        new("R152", ["NoResx_DeclaresAKeyTwice"]),
        new("R158", ["AddASliceChecklist_NamesEveryArtifactAGateForces"]),
    ];
}
