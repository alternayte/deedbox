<!-- snippet: personal-data-event -->
```cs
public record ReviewerInvited(
    string ManuscriptId,
    [property: DataSubject] string ReviewerId,     // whose data this is
    [property: PersonalData] string ReviewerName,  // encrypted under ReviewerId's key
    [property: PersonalData] string? ReviewerEmail);
```
<!-- endSnippet -->
