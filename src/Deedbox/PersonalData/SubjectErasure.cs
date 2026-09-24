using System.Text.Json.Nodes;

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
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        var tenantId = DeedboxContext.ValidTenant(context.TenantId);
        runtime.RequireKeys();

        await DeleteKey(runtime, tenantId, subjectId, ct);
        return await Jobs.Enqueue(runtime, Jobs.Erase, new JsonObject { ["tenantId"] = tenantId, ["subjectId"] = subjectId }, ct);
    }

    public static async Task DeleteKey(DeedboxRuntime runtime, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var connection = runtime.Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await runtime.Provider.DeleteSubjectKey(connection, transaction, tenantId, subjectId, ct);
        await transaction.CommitAsync(ct);
    }
}
