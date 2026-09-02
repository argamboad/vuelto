namespace Perezosoft.Core.Authorization;

/// <summary>
/// A coarse tenant capability (ADR-009). Authorization asks "does the caller's role grant this
/// <see cref="Permission"/>?" via <see cref="RolePermissions"/> — never a scattered
/// <c>role == "owner"</c> test. These are capabilities, <b>not</b> per-resource ACLs; fine-grained
/// per-record sharing is a separate, deferred concern (don't grow this into an ACL system without a
/// new ADR). Add a capability by adding a value here and a row in <see cref="RolePermissions"/>.
/// </summary>
public enum Permission
{
    /// <summary>Read the tenant and its member roster. Every role has this.</summary>
    ViewTenant,

    /// <summary>Rename the tenant.</summary>
    RenameTenant,

    /// <summary>Invite, remove, and manage members and their invitations.</summary>
    ManageMembers,

    /// <summary>Promote/demote members between admin and member (owner-only — privilege escalation).</summary>
    ManageRoles,

    /// <summary>Start checkout and open the billing portal (owner-only — financial).</summary>
    ManageBilling,

    /// <summary>Export the tenant's data — "download my data" (owner-only; GDPR, ADR-011).</summary>
    ExportData,

    /// <summary>Transfer ownership to another member (owner-only).</summary>
    TransferOwnership,

    /// <summary>Dissolve the tenant and delete its data (owner-only).</summary>
    DissolveTenant,

    /// <summary>Create/revoke the tenant's API keys — programmatic tenant access (owner-only; PUBAPI).</summary>
    ManageApiKeys,

    /// <summary>Register/remove the tenant's outbound webhook subscriptions (owner-only; HOOKS).</summary>
    ManageWebhooks,
}
