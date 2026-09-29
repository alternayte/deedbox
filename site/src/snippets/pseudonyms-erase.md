<!-- snippet: pseudonyms-erase -->
```cs
// Erases the person's subject in every period whose secret still exists.
var jobIds = await pseudonyms.EraseIdentityAsync("github:alice");
```
<!-- endSnippet -->
