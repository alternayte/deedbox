namespace Deedbox;

/// <summary>
/// Marks the property that holds whose data an event carries, such as a person ID. <see cref="PersonalDataAttribute"/>
/// fields are encrypted under this subject's key; erasing the subject deletes the key. Use person IDs, not role IDs,
/// so one erasure covers every role a person has.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DataSubjectAttribute : Attribute;

/// <summary>
/// Encrypts the property under its subject's key. The property must be a string or nullable, because after erasure it
/// reads as null, or as the configured placeholder for strings.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersonalDataAttribute : Attribute
{
    /// <summary>
    /// The name of the property that holds the subject ID, for events that carry several subjects. Without it, the
    /// event's single <see cref="DataSubjectAttribute"/> property is used.
    /// </summary>
    public string? Subject { get; set; }
}

/// <summary>
/// Deedbox appends this to each stream that held an erased subject's data. Handle it in projections to scrub what they
/// stored about the subject, and forward it downstream so other systems erase too.
/// </summary>
/// <param name="SubjectId">The erased subject.</param>
public sealed record SubjectErased(string SubjectId);

/// <summary>
/// The tombstone Deedbox appends when a stream is deleted; the stream's earlier events are gone. Handle it in
/// projections to delete what they stored for the stream.
/// </summary>
public sealed record StreamDeleted;
