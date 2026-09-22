namespace SMS.Shared.Common;

/// <summary>
/// Lets one QuestPDF document render at a time in the process.
/// <para>
/// QuestPDF renders correctly one document at a time but not several at once: rendering the same small
/// document from eight threads at once, 13 of 200 came out with the text of every bold run replaced by NUL
/// characters, and 8 of 200 on another run. Rendering the same 200 behind a lock, none did. The fault is in
/// the process-wide state shared between renders, so it is between <i>any</i> two documents, not only two
/// of the same kind: a gate around some of the renders protects those renders from each other, and only them.
/// </para>
/// <para>
/// A document takes milliseconds to render, a very long report a few seconds; the wait is that long at worst.
/// </para>
/// </summary>
public static class PdfRenderGate
{
    private static readonly object Gate = new();

    /// <summary>Runs <paramref name="render"/> — the whole of it, layout and output — with no other gated render running.</summary>
    public static T Run<T>(Func<T> render)
    {
        ArgumentNullException.ThrowIfNull(render);

        lock (Gate) return render();
    }
}
