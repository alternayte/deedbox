<!-- snippet: evolution-records -->
```cs
public record LineAdded(string Sku, int Qty);             // was called ItemAdded

public record ItemAddedV2(string Sku, int Qty);           // the old shape, kept for the typed upcaster

public record ItemPriced(string Sku, int Qty, decimal Price);
```
<!-- endSnippet -->
