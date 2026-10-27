namespace HTBAM.Domain.Entities;

public static class InternalCode
{
    // Full 128-bit random identifier; DB unique indexes remain the final arbiter.
    // No counters, MAX+1, source identifier conversion or update-time regeneration.
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
