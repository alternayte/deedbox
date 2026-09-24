<!-- snippet: personal-data-several -->
```cs
// Several subjects in one event: name each field's subject property.
public record CoAuthorAdded(
    string ManuscriptId,
    string AuthorId,
    [property: PersonalData(Subject = "AuthorId")] string AuthorName,
    string EditorId,
    [property: PersonalData(Subject = "EditorId")] string? EditorNote);
```
<!-- endSnippet -->
