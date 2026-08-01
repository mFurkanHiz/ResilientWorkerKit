using System.Runtime.CompilerServices;

namespace ResilientWorkerKit.Http;

/// <summary>One page of a continuation-token-paginated API response.</summary>
/// <param name="Items">The page's items.</param>
/// <param name="NextContinuationToken">Token for the next page; null/empty on the last page.</param>
public sealed record ContinuationPage<T>(IReadOnlyList<T> Items, string? NextContinuationToken);

/// <summary>One page of a cursor-paginated API response.</summary>
/// <param name="Items">The page's items.</param>
/// <param name="NextCursor">Cursor for the next page; null/empty on the last page.</param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Helpers for draining paginated APIs page by page.</summary>
public static class PageReader
{
    /// <summary>
    /// Streams all items across continuation-token pages. Detects a token that does not
    /// advance (a common API bug that would otherwise loop forever).
    /// </summary>
    public static async IAsyncEnumerable<T> ReadAllAsync<T>(
        Func<string?, CancellationToken, Task<ContinuationPage<T>>> fetchPage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        string? token = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchPage(token, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            if (string.IsNullOrEmpty(page.NextContinuationToken))
            {
                yield break;
            }

            if (page.NextContinuationToken == token)
            {
                throw new InvalidOperationException(
                    "The continuation token did not advance between pages; aborting to avoid an infinite loop.");
            }

            token = page.NextContinuationToken;
        }
    }

    /// <summary>Streams all items across cursor pages (same semantics as the continuation-token overload).</summary>
    public static IAsyncEnumerable<T> ReadAllByCursorAsync<T>(
        Func<string?, CancellationToken, Task<CursorPage<T>>> fetchPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        return ReadAllAsync(
            async (cursor, ct) =>
            {
                var page = await fetchPage(cursor, ct).ConfigureAwait(false);
                return new ContinuationPage<T>(page.Items, page.NextCursor);
            },
            cancellationToken);
    }
}
