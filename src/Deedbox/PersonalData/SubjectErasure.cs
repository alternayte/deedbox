namespace Deedbox;

/// <summary>Erases a data subject: their personal data becomes unreadable everywhere, including stored state and backups' future reads.</summary>
public interface ISubjectErasure
{
    /// <summary>
    /// Deletes the subject's key in the scope's tenant, so their personal data reads as erased at once on every instance.
    /// Then queues a job that appends <see cref="SubjectErased"/> to each stream that held their data and rebuilds those
    /// streams' stored state. The job resumes after a crash. Backups taken before the erasure keep the key until they age out.
    /// </summary>
    /// <param name="subjectId">The subject, such as <c>person:8421</c>.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The erasure job's ID.</returns>
    Task<Guid> EraseSubjectAsync(string subjectId, CancellationToken ct = default);
}

internal sealed class SubjectErasure(DeedboxRuntime runtime, DeedboxContext context) : ISubjectErasure
{
    public async Task<Guid> EraseSubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var tenantId = DeedboxContext.ValidTenant(context.TenantId);
        runtime.RequireKeys();
        return (await Admin.Erase(runtime.Provider, runtime.Clock, tenantId, [subjectId], ct)).JobIds[0];
    }
}

/// <summary>
/// The rules for a subject ID, the same as for a stream ID and on both databases: SQL Server ignores trailing spaces
/// when it compares keys and cuts a longer value to the column, so two subjects could otherwise share one key.
/// </summary>
internal static class SubjectId
{
    public const int MaxLength = 100;

    public static bool IsValid(string? subjectId) =>
        subjectId is { Length: > 0 and <= MaxLength } && !char.IsWhiteSpace(subjectId[0]) && !char.IsWhiteSpace(subjectId[^1]);
}
