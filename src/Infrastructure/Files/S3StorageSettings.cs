namespace Vuelto.Infrastructure.Files;

/// <summary>
/// Settings for <see cref="S3FileStorage"/> (ADR-010), bound from <c>Storage:S3</c>. Because the impl
/// is S3-compatible, the same class targets AWS S3, MinIO, Cloudflare R2, Backblaze B2, etc. — the
/// backend is chosen entirely by these values. <see cref="Bucket"/> being set is what switches storage
/// from local disk to S3 (config-presence, like Stripe-vs-Fake). For non-AWS endpoints set
/// <see cref="Endpoint"/> and usually <see cref="ForcePathStyle"/> = true.
/// </summary>
public sealed class S3StorageSettings
{
    /// <summary>Bucket name. When set, the S3 backend is selected.</summary>
    public string Bucket { get; set; } = "";

    /// <summary>Custom service endpoint for non-AWS providers (e.g. MinIO/R2/B2). Empty ⇒ real AWS.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Region (AWS system name, e.g. <c>us-east-1</c>). Used for signing.</summary>
    public string Region { get; set; } = "";

    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    /// <summary>Path-style addressing (<c>endpoint/bucket/key</c>) — required by MinIO and most non-AWS S3s.</summary>
    public bool ForcePathStyle { get; set; }
}
