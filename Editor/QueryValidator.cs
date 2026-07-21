namespace PickIt.Editor;

public static class QueryValidator
{
    // Validity check via the same loader the engine uses. Returns (ok, error).
    public static (bool ok, string error) Validate(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery)) return (false, "empty query");
        try
        {
            var q = ItemFilterLibrary.ItemQuery.Load(rawQuery);
            return (!q.FailedToCompile, q.FailedToCompile ? (q.Error ?? "failed to compile") : null);
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

