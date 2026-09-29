<!-- snippet: pseudonym-event -->
```cs
public record CorrectionRecorded(
    [property: DataSubject] string Author,   // a pseudonymous subject ID, never the login
    [property: PersonalData] string? Text);  // free text can still name a person
```
<!-- endSnippet -->
