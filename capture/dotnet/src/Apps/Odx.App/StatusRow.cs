namespace Odx.App;

/// <summary>How a status row is marked: green check, amber "!", red cross, or a plain ring for information.</summary>
public enum RowLevel { Ok, Warn, Fail, Info }

/// <summary>
/// Eskimo: one line of the room status or of the quick-check card, shown with its mark instead of a long text block.
/// </summary>
/// <param name="Hint">What to do about it, shown dimmer after the value; empty when there is nothing to do.</param>
public sealed record StatusRow(RowLevel Level, string Label, string Text, string Hint = "");
